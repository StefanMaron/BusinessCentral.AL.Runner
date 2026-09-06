// RecordPatches.NoSourceColumns — per-(table, column) read refusal for columns the runner has
// no source for, so a read names the column instead of handing back a blank.
//
// WHY THIS EXISTS (issue #2771)
//   Object Metadata (2000000071) answers with a row per application-database system table
//   (#2519). Three of its columns have a real source — "Object Type", "Object ID" and
//   "Emit Version", all three primary-key fields — and nine do not: Metadata, "User Code",
//   "User AL Code" and "Symbol Reference" (BLOBs), plus "Metadata Version", Hash,
//   "Object Subtype", "Has Subscribers" and "Schema Hash".
//
//   On a real tier those nine carry the output of PUBLISHING the system app into the
//   application database. The runner never publishes anything into a database and has no such
//   payload, so it handed back BC's own NavValue.GetDefaultNavValue — a 0-byte BLOB, 0, the
//   empty string, false.
//
//   Every one of those is a legitimate value for the column, which is the whole problem:
//
//       ObjectMetadata.CalcFields(Metadata);
//       ObjectMetadata.Metadata.CreateInStream(Stream);   // 0-byte stream, no error
//       if ObjectMetadata."Has Subscribers" then ...      // takes the wrong branch, silently
//
//   .claude/rules/loud-failures.md: a surface the runner cannot answer faithfully must refuse
//   loudly and name itself, never return a default a caller could mistake for real data.
//
// WHY IT WAS NOT DONE IN #2519, AND WHAT CHANGED
//   Refusing one FIELD of an otherwise-valid row needs a per-(table, field) READ seam. Throwing
//   at row-build time is not an option and never was: it would take out FindSet / FindLast /
//   Count / IsEmpty as well, which is the bug #2519 closed wearing a different hat. #2519
//   therefore chose an empty payload over no row at all and recorded the divergence.
//
//   There are two such seams, one per kind of column, and they are different methods because BC
//   reads the two kinds through different machinery (decompiled from Ncl.dll 28.1):
//
//     BLOB    Record.CalcFields(<blob>) -> RecordImplementation.CalcFieldsAsync, which splits
//             its fields into FlowFields and NavBlob fields and sends the BLOBs to
//             DataAccess.GetBlobContentAsync(GetBlobContentCacheRequest). The request carries
//             both halves of the question: MetaApplicationObject (the table) and BlobsToGet
//             (the fields). A cold path — only a CalcFields of a BLOB reaches it.
//
//     SCALAR  A plain AL field read compiles to NavRecord.GetFieldValueSafe(fieldNo,
//             expectedType) -> GetFieldValue(ValidateExpectedType(fieldNo, expectedType)).
//             No data access is involved at all: the value is already in the record buffer, so
//             there is nothing on the provider side to intercept and this is the only seam.
//
// COST — MEASURED, because the guess was wrong by four orders of magnitude
//   Prepending to GetFieldValueSafe looked alarming: it is the method a plain AL field read
//   compiles to, so "every field read in the process" suggested millions of calls per run. It
//   is not. Counted with a temporary Interlocked counter in the guard itself, on this build:
//
//       al-language corpus, 2,610 tests   ->  ~45,600 guard calls
//       runner-extras,        300 tests   ->  ~38,100 guard calls
//
//   At roughly ten instructions for the common path that is well under a million instructions,
//   against 209-268 BILLION instructions:u for the same corpus run (perf stat, three warm runs).
//   It is not measurable, and the wall-clock spread between identical warm corpus runs on this
//   box (29.5s to 73.6s) is orders of magnitude larger than anything this could contribute, so
//   no wall-clock A/B could have resolved it either — which is why the count is the measurement
//   quoted here rather than a timing.
//
//   The guard is still written for the cheap case, because that is free to do: null check,
//   one property call, one REFERENCE compare against the last metatable seen, return. No
//   dictionary lookup, no string work, no metatable walk and no allocation for any table that
//   is not registered. It takes NavRecord directly rather than object, so MetaTable is a plain
//   property call and not a reflected GetValue.
//
//   What the low count also means is that GetFieldValueSafe is NOT the only way AL reaches a
//   field value — 17 calls per corpus test is too few for that. It is the route a direct
//   `Rec."Column"` read takes, which is what the proving test exercises and what #2771 reported.
//   A RecordRef/FieldRef read, or a page or report binding, may reach the buffer another way and
//   would still see the blank; none of those is reachable for 2000000071 (see below), but that
//   is a property of this table, not of the seam.
//
// WHAT THIS DELIBERATELY DOES NOT COVER
//   RecordRef/FieldRef reads reach the buffer by a different route and are not guarded here;
//   2000000071 cannot be opened as a RecordRef on a Cloud-target app anyway
//   (NavRecordRef.CheckIsOpenAllowed refuses every id in SystemTables.InternalTables, measured
//   on all 8 BC legs of corpus run 33968379281). The 3-argument GetFieldValueSafe overload is
//   the tableextension route and cannot reach a base-table field, which is what all nine of
//   these are.
using System.Collections;
using System.Reflection;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// The columns the runner has no source for, by table id and by COLUMN NAME.
    ///
    /// <para>By name, never by field number, for the same reason this file's neighbours resolve
    /// option ordinals by name: the field numbers of Object Metadata are sparse (3, 6, 9, 15,
    /// 18, 27, 30, 33, 34, 35, 36, 37) and belong to Microsoft, so hardcoding them would make a
    /// renumbering read as "this column now has a source". A name that stops matching produces a
    /// column that reads blank again, which the runner-extras suite fails on — a loud outcome
    /// either way.</para>
    ///
    /// <para>Adding a table here is a real change and needs its own RED → GREEN, because it
    /// changes what AL sees. The nearest candidate is Object (2000000001), whose own patch file
    /// names itself "a second consumer" of this seam and lists eleven columns it answers with
    /// BC's default; tests/runner-extras/object-system-table currently ASSERTS those blanks, so
    /// flipping it is a behaviour change to that suite rather than a line here, and the blast
    /// radius past this repo's own tests is not measured. Issue #3096 tracks it. The Table /
    /// Page / Report / CodeUnit
    /// Metadata virtual tables are named in #2771 as likely reusers of the seam; unlike Object
    /// they have no runner-extras suite pinning their blanks, so what they answer today is not
    /// written down anywhere and would have to be established first.</para>
    /// </summary>
    private static readonly Dictionary<int, string[]> _noSourceColumnNames = new()
    {
        [ObjectMetadataSystemTableId] = new[]
        {
            // The compiled-metadata payload: what publishing the system app into an application
            // database writes. The runner publishes nothing, so there is no source for any of
            // them. Split by kind only to document which seam catches which.
            "Metadata",            // BLOB
            "User Code",           // BLOB
            "User AL Code",        // BLOB
            "Symbol Reference",    // BLOB
            "Metadata Version",
            "Hash",
            "Object Subtype",
            "Has Subscribers",
            "Schema Hash",
        },
    };

    /// <summary>Per-metatable resolved field numbers, built once from the metatable's own field
    /// list. Keyed by the NCLMetaTable INSTANCE rather than by table id: the field numbers come
    /// off that instance, and a run can hold more than one metatable for an id across a bundle
    /// reload.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Dictionary<int, string>>
        _noSourceFieldsByMetaTable = new();

    /// <summary>
    /// The last metatable the scalar guard resolved to "this table has NO columns the runner
    /// lacks a source for". One field, and it only ever holds a metatable for which
    /// <see cref="NoSourceFieldsFor"/> returned null — those two properties together are what
    /// make a wrong answer unrepresentable rather than merely unlikely.
    ///
    /// <para>THIS USED TO BE TWO FIELDS — the metatable and its resolved field map — published
    /// with two separate plain stores, under a comment claiming a torn pair "can only ever cost
    /// a redundant lookup, never a wrong answer". That claim was FALSE, and both directions are
    /// wrong answers rather than slow ones. With the stores ordered `fields` then `table`, and
    /// thread A on the registered table while thread B is on any other table:</para>
    ///
    /// <list type="bullet">
    ///   <item><description>A stores its map, B stores null, A stores the registered table →
    ///   the pair reads (registered table, no columns), so the fast path returns and the
    ///   refusal is SILENTLY SKIPPED — the blank goes back to AL, which is the exact silent
    ///   default this whole file exists to remove (.claude/rules/loud-failures.md).</description></item>
    ///   <item><description>B stores null, A stores its map, B stores its own table → the pair
    ///   reads (unrelated table, registered map), so an ordinary table SPURIOUSLY REFUSES at
    ///   field numbers 3/6/9/15/18/27/30/33-37. The guard is prepended for every table in the
    ///   process, so that blast radius is not confined to Object Metadata.</description></item>
    /// </list>
    ///
    /// <para>A single field cannot be torn, and the invariant above ("only a table with nothing
    /// registered is ever stored here") means a stale read costs a re-lookup and nothing else.
    /// Reads and writes stay unsynchronised deliberately: a reference store is atomic, and the
    /// only value the field can carry is one whose answer is "nothing to refuse".</para>
    ///
    /// <para>The window is narrow today but real: AL runs on one thread at a time, except that
    /// <c>TestExecutor.InvokeWithTimeout</c> does not abort a timed-out test's thread, so an
    /// abandoned test keeps executing AL alongside the next one. <c>--jobs</c> shards across
    /// worker PROCESSES, so it does not widen it. Narrow is not a reason to keep a comment that
    /// says the shape is safe.</para>
    /// </summary>
    private static object? _noSourceLastTableWithNothingRegistered;

    // ── ROW PROVENANCE: --test-data BEATS THE REFUSAL ───────────────────────────────────────
    //
    // A registered column has no source only because the RUNNER SYNTHESISED the row. 2000000071
    // is not a virtual table — it is a real application-database system table, so a --test-data
    // backup can genuinely carry rows for it, with a genuine published payload in exactly the
    // nine columns this file refuses.
    //
    // RecordPatches.ObjectMetadataSystemTable.cs already states that precedence in its header —
    // "Real rows always win over synthesised ones" — and implements it with
    // `if (ProviderHasAnyRow(provider)) return;`. The refusal did not honour it. Keyed on
    // TableId alone, it raised out-of-scope over rows that HAVE a source: a loud failure on
    // correct data, and the mirror image of the spurious-refusal half of the torn-pair bug
    // reached by a different route.
    //
    // Reachable, not theoretical: docs/limitations.md names Microsoft's Tests-SINGLESERVER
    // bucket as the settling route for this table, that bucket is OnPrem-target and reads
    // 2000000071 directly, and --test-data is mandatory for those buckets.
    //
    // WHY ONE PROCESS-WIDE FLAG RATHER THAN ONE PER PROVIDER. The populate is per provider and a
    // run can create several stores, so per-provider looks like the tighter scope. It is not
    // reachable as a MIXED state: 2000000071 holds no company-scoped data, so a backup either
    // carries Object Metadata rows — and every freshly created store is hydrated from it before
    // the populator sees it — or it carries none and every store synthesises. The flag is
    // therefore uniform across the stores of a run wherever it is uniform at all.
    //
    // It is also deliberately ONE-WAY, which is what makes a mixed run safe rather than merely
    // unlikely: once any store is found holding real rows, no later synthesising store can
    // re-arm the refusal, so the failure mode this fix removes cannot come back through the
    // ordering of two stores. The cost of that choice is the opposite error — a synthesised
    // store read after a real one would return a blank instead of refusing — which is #2771's
    // original silent blank, strictly less bad than failing loudly over correct data, and
    // unreachable for the reason above.
    private static volatile bool _objectMetadataRowsAreReal;

    /// <summary>
    /// True when this run has seen an Object Metadata store that already held rows — i.e.
    /// --test-data (or an install baseline) supplied them and
    /// <c>PopulateObjectMetadataSystemTable</c> left the store alone.
    /// </summary>
    public static bool ObjectMetadataRowsAreReal => _objectMetadataRowsAreReal;

    /// <summary>
    /// Called by <c>PopulateObjectMetadataSystemTable</c> on the branch where
    /// <c>ProviderHasAnyRow</c> answered true, i.e. exactly where it declines to synthesise.
    /// One-way on purpose — see the block comment above.
    /// </summary>
    public static void MarkObjectMetadataRowsAreReal() => _objectMetadataRowsAreReal = true;

    /// <summary>Test hook. The flag is process-wide, so a test that sets it has to put it back.</summary>
    internal static void ResetObjectMetadataRowProvenanceForTests() => _objectMetadataRowsAreReal = false;

    /// <summary>
    /// Whether the no-source refusal should fire for <paramref name="tableId"/> at all. False for
    /// every unregistered table, and false for Object Metadata once its rows are known to be
    /// real. The single gate both seams pass through, so the BLOB path and the scalar path
    /// cannot disagree about provenance.
    /// </summary>
    public static bool NoSourceRefusalIsActiveFor(int tableId)
    {
        if (!_noSourceColumnNames.ContainsKey(tableId)) return false;
        if (tableId == ObjectMetadataSystemTableId && _objectMetadataRowsAreReal) return false;
        return true;
    }

    /// <summary>True when <paramref name="metaTable"/> is the one the scalar guard last resolved
    /// to "nothing registered". Only ever a fast path — a false answer costs a re-lookup.</summary>
    internal static bool IsKnownToHaveNoNoSourceColumns(object metaTable)
        => ReferenceEquals(metaTable, _noSourceLastTableWithNothingRegistered);

    /// <summary>Record that <paramref name="metaTable"/> has no registered no-source columns.
    /// The ONLY writer of <see cref="_noSourceLastTableWithNothingRegistered"/>, and the reason
    /// the field's invariant holds: call it only after a lookup returned null.</summary>
    internal static void RememberHasNoNoSourceColumns(object metaTable)
        => _noSourceLastTableWithNothingRegistered = metaTable;

    /// <summary>
    /// The no-source columns of <paramref name="metaTable"/> as fieldNo → column name, or null
    /// when the table has none. Null is the answer for every table but a registered one, and it
    /// is what the two guards below return on.
    /// </summary>
    private static Dictionary<int, string>? NoSourceFieldsFor(NCLMetaTable? metaTable)
    {
        if (metaTable == null) return null;
        if (!_noSourceColumnNames.TryGetValue(metaTable.TableId, out var names)) return null;
        if (!NoSourceRefusalIsActiveFor(metaTable.TableId)) return null;

        if (_noSourceFieldsByMetaTable.TryGetValue(metaTable, out var cached)) return cached;

        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<int, string>();
        foreach (var field in GetAllFields(metaTable) ?? Enumerable.Empty<NCLMetaField>())
        {
            var name = field.FieldName;
            if (name != null && wanted.Contains(name)) map[field.FieldNo] = name;
        }

        // A registered table whose metatable names NONE of its registered columns is a shape the
        // runner cannot act on, and it is exactly the silent regression this registry exists to
        // prevent: every read would go back to reading blank with nothing to notice it. Say so
        // rather than caching an empty map (loud-failures.md).
        if (map.Count == 0)
            throw ObjectMetadataShapeGap(
                $"table {metaTable.TableId} is registered as having columns with no runner source "
                + $"({string.Join(", ", names)}), but its metatable declares none of them by those "
                + "names, so a read of one could not be refused and would silently answer blank");

        _noSourceFieldsByMetaTable.AddOrUpdate(metaTable, map);
        return map;
    }

    /// <summary>The refusal one no-source column read raises. The API names the COLUMN, not just
    /// the table: a refusal naming only "Object Metadata" tells a developer nothing about which
    /// read to stop making, and nine columns would raise nine indistinguishable errors.</summary>
    private static RunnerOutOfScopeException NoSourceColumnRefusal(int tableId, string columnName)
        => new(
            $"{NoSourceTableCaption(tableId)}.\"{columnName}\" (system table {tableId})",
            // "not-yet-implemented" prefix on purpose, and it is load-bearing rather than
            // cosmetic: ApplicationObjectBasePatches.IsPermanentOutOfScope traps a PERMANENTLY
            // out-of-scope refusal into `false` for an AL [TryFunction], which is right when a
            // real BC environment also lacks the surface. Real BC HAS these columns populated —
            // the runner is the one without a source — so trapping would turn a runner gap into
            // a clean `if not TryX() then`, the silent default this refusal exists to prevent.
            "not-yet-implemented — object-metadata-payload — the runner publishes nothing into an "
            + "application database, so this column has no source and a blank would be "
            + "indistinguishable from a real empty value",
            ObjectMetadataDocLink);

    /// <summary>Human name for a registered table, for the refusal message only. A switch rather
    /// than the metatable's caption: the caption is localisable and the message is a contract
    /// AL tests match on.</summary>
    private static string NoSourceTableCaption(int tableId) => tableId switch
    {
        ObjectMetadataSystemTableId => "Object Metadata",
        _ => $"table {tableId}",
    };

    // ── The BLOB seam ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Refuse a read of <paramref name="field"/> when the runner has no source for it. Called
    /// from the runner's OWN blob-load site — FlowFieldPatches.RecordImpl_CalcFieldsAsync_3 —
    /// because <c>DataAccess.GetBlobContentAsync</c>, the seam BC itself uses and the one
    /// #2771's issue body proposed, is never reached from AL here: NclCecilRewrite.Records.cs
    /// replaces both <c>RecordImplementation.CalcFieldsAsync</c> overloads outright, and the
    /// replacement loads BLOBs off the in-memory provider directly. Measured, not assumed — a
    /// prepend on GetBlobContentAsync left the proving test failing.
    ///
    /// <para>The owning table comes from the field's own <c>Parent</c> rather than from a
    /// caller-supplied metatable, so every call site is one argument and cannot pass a table
    /// that disagrees with the field.</para>
    /// </summary>
    internal static void ThrowIfColumnHasNoSource(NCLMetaField? field)
    {
        if (field == null) return;
        var fields = NoSourceFieldsFor(field.Parent as NCLMetaTable);
        if (fields != null && fields.TryGetValue(field.FieldNo, out var name))
            throw NoSourceColumnRefusal(((NCLMetaTable)field.Parent!).TableId, name);
    }

    // ── The SCALAR seam ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Prepended to <c>NavRecord.GetFieldValueSafe(int fieldNo, NavType expectedType)</c> — the
    /// method a plain AL field read compiles to — for every table.
    ///
    /// <para>It takes <c>NavRecord</c> directly rather than <c>object</c>: the IL slot already
    /// is a NavRecord and <c>MetaTable</c> is accessible from C# (other patches in this
    /// directory read it the same way), so the common case is a null check, one property call
    /// and one REFERENCE compare against the last metatable seen — no reflected
    /// <see cref="PropertyInfo"/> <c>GetValue</c>, which is what the first version did.</para>
    ///
    /// <para>That shape was chosen expecting a hot path. Measured, it is not one: ~45,600 calls
    /// across the whole 2,610-test corpus and ~38,100 across runner-extras. See the file
    /// header — the number is recorded there so nobody re-derives the wrong fear, and so the
    /// claim in this comment is a measurement rather than an intuition.</para>
    ///
    /// <para>The two-argument overload only. The three-argument one carries an extension object
    /// id and serves tableextension fields, and all nine registered columns are base-table
    /// fields of a Microsoft system table, which no extension can restate.</para>
    /// </summary>
    public static void NavRecord_NoSourceColumnGuardForRead(NavRecord self, int fieldNo)
    {
        var metaTable = self?.MetaTable;
        if (metaTable == null) return;

        // The fast path, and the whole cache: the last metatable known to have NOTHING
        // registered. It is deliberately one-sided. A NEGATIVE answer is the only thing worth
        // caching here — it covers every table in the process but the one — and caching only
        // the negative is what lets the cache be a single field, which is what makes a wrong
        // answer unrepresentable instead of merely improbable. See the field's own comment for
        // the two wrong answers the two-field version could produce.
        if (IsKnownToHaveNoNoSourceColumns(metaTable)) return;

        // Not the remembered table. Resolve it properly — for a registered table this is a
        // one-entry dictionary lookup plus a ConditionalWeakTable hit, and a registered table
        // is never cached here, so it pays that on every read. Object Metadata is read rarely;
        // correctness is worth more than a cached positive.
        var fields = NoSourceFieldsFor(metaTable);
        if (fields == null)
        {
            RememberHasNoNoSourceColumns(metaTable);
            return;
        }
        if (fields.TryGetValue(fieldNo, out var name))
            throw NoSourceColumnRefusal(metaTable.TableId, name);
    }
}
