// SeededRowColumns — the columns one seeded system-table row intends to write, and whether
// each one could actually be written (AlRunner#3015).
//
// WHY THIS EXISTS
//   The runner seeds rows a real service tier would have written before any AL ran: Company
//   (2000000006) at company-create time, Published Application (2000000206) and Installed
//   Application (2000000212) at publish time. Each seeder locates its columns by NAME off the
//   metatable rather than by a hardcoded ordinal, which is the right choice — an ordinal
//   writes into whatever column happens to sit at that index once BC's metadata moves.
//
//   What was wrong was the failure branch. Both seeders' local `Set` helper read:
//
//       void Set(string fieldName, object? value)
//       {
//           if (value == null) return;
//           if (!fieldByName.TryGetValue(fieldName, out var f)) return;   // <-- silent
//           var idx = f.FieldIndex;
//           if (idx < 0 || idx >= values.Length) return;                  // <-- silent
//           ...
//       }
//
//   Exactly one column per table was hard-checked (Published Application's "ID", Company's
//   "Name"). Every other column was best-effort: a rename left NCLMetaField.EmptyValue in the
//   slot, the row was STILL inserted and STILL found by its key, and nothing said so.
//
//   That matters most on "Runtime Package ID", because it exists to be compared.
//   Reten. Pol. Allowed Tbl. Impl.ModuleOwnsTable:
//
//       if AllObj."App Runtime Package ID" <> PublishedApplication."Runtime Package ID" then
//           exit(false);
//
//   With the column left at its default the comparison declines for EVERY app, and BC logs a
//   warning rather than raising — so the failure is invisible from AL as well as from the
//   runner. The four "Version …" columns are worse still: BC SetRanges on them before it ever
//   reads the package id, so a rename there makes the FindFirst miss and the app simply looks
//   unpublished. Both are the silent default .claude/rules/loud-failures.md forbids.
//
//   The third sibling seeder already got this right — FieldByNameOnUser in
//   RecordPatches.UserSystemTable.cs throws, citing that same rule. Two paths writing the same
//   kind of state, one with the guard and one without, is a defect whether or not anyone has
//   hit it yet. This type is the shared guard.
//
// WHY IT THROWS RATHER THAN REPORTING
//   The Published Application DEPENDENCY rows are seeded inside the install-baseline cache
//   MISS branch, and that snapshot is PERSISTED to disk. A stderr line there fires once, on
//   the run that bakes the wrong row set into the cache, and there is nothing at all to read
//   on every later run that restores it as a HIT. Refusing is the only report that survives a
//   cache. RecordPatches.PublishedApplicationSystemTable.cs already makes exactly this
//   argument for a partial row set; an incomplete row is the same failure one level down.
//
//   The cost of refusing is bounded and the message pays it back: it names the table, every
//   column that could not be written, why each one could not, and the field list the metatable
//   actually states — so a BC rename is a one-line fix rather than an investigation. There is
//   no correctness-preserving alternative, because the row would otherwise be inserted wrong.
//
//   The Company seeder is the one deliberate exception, and its caller — not this type — makes
//   it: that row is seeded per app group OUTSIDE the persisted cache, so its existing
//   catch-and-report reaches stderr on every run rather than once. See
//   RecordPatches.CompanySystemTable.cs.
//
// WHY IT IS GENERIC
//   So it can be unit-tested. The real callers instantiate it over BC's NCLMetaField, which a
//   test cannot construct and cannot make lose a column; SeededRowColumnsTests instantiates it
//   over a plain tuple and drives the identical resolution, range check, aggregation and
//   message construction.
using System.Diagnostics.CodeAnalysis;

namespace AlRunner.Patches;

/// <summary>
/// Resolves the columns of one seeded system-table row by name, and refuses to let a column
/// the caller asked to write go unwritten. See this file's header for why (AlRunner#3015).
/// </summary>
/// <typeparam name="TField">
/// The metatable's field type — <c>NCLMetaField</c> in production, a plain tuple in tests.
/// </typeparam>
internal sealed class SeededRowColumns<TField>
{
    private readonly string _tableLabel;
    private readonly IReadOnlyDictionary<string, TField> _fieldByName;
    private readonly Func<TField, int> _slotOf;
    private readonly Func<TField, string> _describeField;
    private readonly int _slotCount;
    private readonly List<string> _unwritable = new();

    /// <param name="tableLabel">How the table is named in the refusal, e.g. <c>Company (2000000006)</c>.</param>
    /// <param name="fieldByName">
    /// The metatable's fields keyed by name. Supply the SAME dictionary the caller writes
    /// values through, and with the same comparer — the seeders use OrdinalIgnoreCase — so a
    /// column resolves here exactly when it resolves there.
    /// </param>
    /// <param name="slotOf">The field's index into the row's value array (<c>FieldIndex</c>).</param>
    /// <param name="describeField">How one field appears in the refusal's field list.</param>
    /// <param name="slotCount">How many value slots the row has.</param>
    internal SeededRowColumns(
        string tableLabel,
        IReadOnlyDictionary<string, TField> fieldByName,
        Func<TField, int> slotOf,
        Func<TField, string> describeField,
        int slotCount)
    {
        _tableLabel = tableLabel;
        _fieldByName = fieldByName;
        _slotOf = slotOf;
        _describeField = describeField;
        _slotCount = slotCount;
    }

    /// <summary>Every column asked for that could not be written, with the reason, in ask order.</summary>
    internal IReadOnlyList<string> Unwritable => _unwritable;

    /// <summary>
    /// Resolve <paramref name="fieldName"/> to the metatable field and the value slot it
    /// writes into. False means the column cannot be written, and RECORDS that fact — the
    /// caller may return, because <see cref="ThrowIfAnyColumnCouldNotBeWritten"/> will raise
    /// it before the row is inserted. Every call is a declaration that the column is required:
    /// there is no separate list to keep in step with the writes.
    /// </summary>
    internal bool TryResolve(string fieldName, [MaybeNullWhen(false)] out TField field, out int slot)
    {
        if (!_fieldByName.TryGetValue(fieldName, out field))
        {
            Record(fieldName, "no field of that name");
            field = default;
            slot = -1;
            return false;
        }

        slot = _slotOf(field);
        if (slot < 0 || slot >= _slotCount)
        {
            Record(fieldName,
                $"resolves to slot {slot}, outside this row's {_slotCount} value slot(s)");
            field = default;
            slot = -1;
            return false;
        }

        return true;
    }

    private void Record(string fieldName, string why)
    {
        var entry = $"\"{fieldName}\" — {why}";
        // Once per column, not once per attempt: the Published Application seeder builds one
        // row per loaded app, and a repeated name would otherwise multiply the message by the
        // number of modules.
        if (!_unwritable.Contains(entry, StringComparer.Ordinal))
            _unwritable.Add(entry);
    }

    /// <summary>
    /// Raise if any column the caller asked to write could not be. Call after the row is
    /// filled and BEFORE it is inserted — a row that reaches the provider incomplete is
    /// indistinguishable from a correct one at every later read.
    /// </summary>
    internal void ThrowIfAnyColumnCouldNotBeWritten()
    {
        if (_unwritable.Count == 0) return;

        throw new InvalidOperationException(
            $"[SeededRow] {_tableLabel}: could not write {_unwritable.Count} of the column(s) this "
            + "row is built from — " + string.Join("; ", _unwritable)
            + ". BC metadata shape changed; the row would otherwise be inserted with BC's own "
            + "default in that column and every later read would look correct. Metatable states "
            + $"[fields={string.Join("/", _fieldByName.Values.Select(_describeField))}] "
            + "— see AlRunner#3015.");
    }
}
