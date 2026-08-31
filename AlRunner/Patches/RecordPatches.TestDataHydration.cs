// RecordPatches.TestDataHydration — the MECHANISM half of --test-data (issue #2258): turn
// decoded backup rows into NavValues and put them in the in-memory store.
//
// This file deliberately knows nothing about WHICH tables get hydrated or WHEN. That is
// TestDataProvisioner's job. The split is not tidiness: the eager "hydrate everything before
// the install triggers" policy is right for a CRONUS-sized demo database and wrong for a
// customer backup with millions of rows, where the load has to become per-table and on
// demand. Moving it should be a change of CALL SITE, not a rewrite, so nothing below assumes
// it runs before anything in particular.
//
// WHY NOT AL `Insert`
//   Restoring a database in real BC fires no OnInsert trigger and runs no validation, so
//   replaying demo data through AL Insert would produce a different starting state than a
//   restore does. Rows therefore go straight into the TempTableDataProvider, exactly the way
//   RestoreInstallBaselineSnapshot puts a captured baseline back.
//
// FAITHFULNESS / REFUSAL (.claude/rules/loud-failures.md)
//   Values are rebuilt through BC's OWN codec, NavValue.CreateNavValueFromObject, handed the
//   target field's metadata. Any value this file cannot prove it rebuilds identically aborts
//   THAT TABLE's hydration with a message naming the table, the column and the type — it
//   never substitutes a default and never leaves a partially-built row in the store.
//
// TABLE-EXTENSION FIELDS (issue #2261)
//   BC splits an extended table across the base table and a `<table>$ext` companion. The
//   reader joins them on request, and the joined row arrives here keyed by AL field name for
//   every extending app the reader was given symbols for. Two consequences are handled below,
//   and neither may be silent:
//
//   a) A column for an app OUTSIDE this run's closure arrives in its raw BC storage form,
//      `<sql name>$<app id>`, because the reader had no symbols to name it with. The AL record
//      this run builds has no such field — the app is not installed here — so the column is
//      dropped, counted, and reported. Anything else that fails to resolve still REFUSES the
//      table: a bare unresolvable name could equally be a schema mismatch, and the two must
//      not be confused.
//
//   b) The merge can fail to happen at all without failing. Measured on the shipped reader:
//      `--mergeExtensions` (camelCase) is accepted by the CLI, ignored, and exits 0 — which
//      would hydrate `Source Code Setup` with its ONE own field, ~50 blanks, and no error
//      anywhere. That guard is NOT here, deliberately: this metatable cannot answer "is this
//      field stored in the companion". Measured on `Return Reason` (6635), whose
//      `Default Location Code` lives in `Return Reason$ext` in the backup — the runner's
//      NCLMetaField for it reports IsCompanionTableField = false, because BC only sets that
//      flag when SourceExtensionType is ModernDev and the runner's metadata construction
//      leaves it None. So the guard lives once per run, in TestDataProvisioner, where it is
//      answered by the reader instead of by our own metadata.
//
// WHAT IS DELIBERATELY NOT HYDRATED, AND IS SAID OUT LOUD
//   BC's system columns (`timestamp`, `$systemId`, `$systemCreatedAt`, `$systemCreatedBy`,
//   `$systemModifiedAt`, `$systemModifiedBy`) carry no AL field id in the reader's schema
//   output, so mapping them back to AL fields 2000000000-2000000004 would rest on a
//   convention no service tier has confirmed here. They are left at the field's own BC
//   default (NavValue.CreateNavValueFromObject(field, null), i.e. what Record.Init() gives)
//   and reported in the hydration summary. See the issue for the follow-up.
using AlRunner.Infrastructure;
using System.Reflection;
using System.Text.Json;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

/// <summary>Raised when a table's rows cannot be rebuilt faithfully. Caught per table by
/// TestDataProvisioner, which reports it and moves on — the table is left EMPTY rather than
/// half-populated.</summary>
internal sealed class TestDataHydrationRefusal : Exception
{
    internal TestDataHydrationRefusal(string message) : base(message) { }
}

public static partial class RecordPatches
{
    /// <summary>Column names the reader emits for BC's own bookkeeping. See the file header
    /// for why they are excluded rather than mapped.</summary>
    internal static readonly IReadOnlySet<string> TestDataSystemColumnNames =
        new HashSet<string>(StringComparer.Ordinal)
        { "timestamp", "$systemId", "$systemCreatedAt", "$systemCreatedBy", "$systemModifiedAt", "$systemModifiedBy" };

    /// <summary>The outcome of one table's hydration: how many rows landed, and how many
    /// merged columns belonged to an app this run does not have installed. The second number
    /// is reported rather than left implicit — see the file header, case (a).</summary>
    internal readonly record struct TestDataTableResult(int Rows, int ColumnsFromUninstalledApps);

    /// <summary>
    /// Insert <paramref name="rows"/> into <paramref name="tableId"/>'s in-memory store.
    /// <paramref name="rows"/> is one dictionary per row, keyed by the AL field NAME the
    /// reader emitted (BC's system columns already dropped by the caller).
    ///
    /// Every key must resolve to a field of the target NCLMetaTable — the metatable the row is
    /// actually inserted into — with ONE declared exception: a table-extension storage column
    /// (`&lt;sql name&gt;$&lt;app id&gt;`) owned by an app outside this run's closure is dropped and
    /// counted, because this run's AL record genuinely has no such field. Any OTHER key that
    /// does not resolve refuses the table rather than being dropped: an unresolvable bare name
    /// means the reader decoded a column this runner build has no AL field for, and hydrating
    /// the rest would ship a knowingly incomplete record.
    ///
    /// Throws <see cref="TestDataHydrationRefusal"/> BEFORE touching the store if any value
    /// cannot be rebuilt, so a refusal never leaves rows behind.
    /// </summary>
    internal static TestDataTableResult HydrateTestDataTable(
        int tableId, string tableNameForDiagnostics,
        IReadOnlyList<IReadOnlyDictionary<string, JsonElement>> rows)
    {
        if (rows.Count == 0) return new TestDataTableResult(0, 0);

        var meta = EnsureTableInMetadataCache(tableId)
            ?? throw new TestDataHydrationRefusal(
                $"table {tableId} '{tableNameForDiagnostics}': this process has no NCLMetaTable for it, "
                + "so its rows cannot be turned into AL records");

        var source = ResolveSkeletonDataAccessSource()
            ?? throw new TestDataHydrationRefusal(
                $"table {tableId} '{tableNameForDiagnostics}': the skeleton session has no DataAccessSource yet");

        var fieldByName = new Dictionary<string, NCLMetaField>(StringComparer.Ordinal);
        for (var fi = 0; fi < meta.FieldCount; fi++)
        {
            var f = meta.GetFieldByIndex(fi);
            fieldByName[f.FieldName] = f;
        }

        var droppedColumns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in rows.SelectMany(r => r.Keys).Distinct(StringComparer.Ordinal))
        {
            if (fieldByName.ContainsKey(name)) continue;
            if (BackupCatalog.TryParseUnresolvedExtensionColumn(name, out _, out _))
            {
                // Case (a): a companion column for an app outside this run's closure. Dropped
                // and counted — BuildTestDataRow never looks it up, because it indexes the row
                // by the metatable's own field names.
                droppedColumns.Add(name);
                continue;
            }
            throw new TestDataHydrationRefusal(
                $"table {tableId} '{tableNameForDiagnostics}': the backup has a column '{name}' that is "
                + "not a field of the AL table this runner build would insert into, so the rows cannot "
                + "be rebuilt faithfully");
        }

        // Build EVERY row first. A refusal in row 900 must not leave rows 1-899 in the store.
        var built = new NavValue[rows.Count][];
        for (var ri = 0; ri < rows.Count; ri++)
            built[ri] = BuildTestDataRow(meta, tableId, tableNameForDiagnostics, rows[ri], fieldByName);

        var perTable = _dataAccessByTable.GetValue(source,
            static _ => new System.Collections.Concurrent.ConcurrentDictionary<int, object>());
        var dataAccess = perTable.GetOrAdd(tableId, _ => _mCreateTempDataAccess!.Invoke(source, new object[] { meta })!);
        var provider = GetDataProvider(dataAccess)
            ?? throw new TestDataHydrationRefusal(
                $"table {tableId} '{tableNameForDiagnostics}': its data access exposes no in-memory DataProvider");

        var insert = provider.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m => m.Name == "Insert" && m.GetParameters().Length == 4
                     && m.GetParameters()[0].ParameterType == typeof(int));
        var insertOptions = Enum.ToObject(insert.GetParameters()[2].ParameterType, 0);

        _ibMutableBufferCtor ??= typeof(ReadOnlyRecordBuffer).Assembly
            .GetType("Microsoft.Dynamics.Nav.Runtime.MutableRecordBuffer")
            ?.GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null, types: new[] { typeof(ReadOnlyRecordBuffer) }, modifiers: null)
            ?? throw new InvalidOperationException(
                "MutableRecordBuffer(ReadOnlyRecordBuffer) not found — BC metadata shape changed");

        foreach (var values in built)
        {
            var readOnly = new ReadOnlyRecordBuffer(meta, values);
            var mutable = _ibMutableBufferCtor.Invoke(new object[] { readOnly });
            insert.Invoke(provider, new object?[] { 0, mutable, insertOptions, null });
        }
        return new TestDataTableResult(built.Length, droppedColumns.Count);
    }

    private static NavValue[] BuildTestDataRow(
        NCLMetaTable meta, int tableId, string tableName,
        IReadOnlyDictionary<string, JsonElement> row, IReadOnlyDictionary<string, NCLMetaField> fieldByName)
    {
        var values = new NavValue[meta.FieldCount];
        for (var fi = 0; fi < meta.FieldCount; fi++)
        {
            var field = meta.GetFieldByIndex(fi);
            var metadata = (INavValueMetadata)field;
            if (!row.TryGetValue(field.FieldName, out var json))
            {
                // No stored value for this field in the backup: a FlowField, a system column
                // (see the file header), or a field this app version added. BC's own default
                // for the field's type — the same value Record.Init() produces — not a guess
                // at what the source "probably" held.
                values[fi] = NavValue.CreateNavValueFromObject(metadata, null);
                continue;
            }
            values[fi] = ConvertTestDataValue(
                metadata, json, tableId, tableName, field.FieldNo, field.FieldName);
        }
        return values;
    }

    /// <summary>
    /// Rebuild one decoded backup value as a NavValue for <paramref name="metadata"/>'s type.
    ///
    /// Every branch hands a CLR object to BC's own <see cref="NavValue.CreateNavValueFromObject"/>
    /// — this method never encodes a NavValue itself. A type that is not listed is refused, by
    /// design: silently guessing an encoding for (say) a DateFormula, whose SQL form is an
    /// opaque BC-internal string, is exactly the failure this feature exists to avoid.
    /// </summary>
    internal static NavValue ConvertTestDataValue(
        INavValueMetadata metadata, JsonElement json, int tableId, string tableName, int fieldNo, string columnName)
    {
        string Refuse(string why) =>
            $"table {tableId} '{tableName}', column '{columnName}' (AL field {fieldNo}, {metadata.NclType}): {why}";

        var nclType = metadata.NclType;
        var isStringLike = nclType is NavNclType.NavText or NavNclType.NavCode
            or NavNclType.NavOemText or NavNclType.NavOemCode;

        if (json.ValueKind == JsonValueKind.Null)
        {
            // A DB NULL. Only the string-like types have a NavValue that can represent one
            // (the same restriction BC's own byte codec has — see
            // RecordPatches.InstallBaselineDisk's KindNullString).
            if (!isStringLike)
                throw new TestDataHydrationRefusal(Refuse(
                    "the backup holds a NULL, and only Text/Code have a NavValue that can represent one"));
            return nclType switch
            {
                NavNclType.NavText or NavNclType.NavOemText =>
                    new NavText(metadata.NavDefinedLengthMetadata, (string?)null),
                _ => new NavCode(metadata.NavDefinedLengthMetadata, (string?)null),
            };
        }

        switch (nclType)
        {
            case NavNclType.NavText:
            case NavNclType.NavCode:
            case NavNclType.NavOemText:
            case NavNclType.NavOemCode:
                if (json.ValueKind != JsonValueKind.String)
                    throw new TestDataHydrationRefusal(Refuse($"expected a JSON string, got {json.ValueKind}"));
                return NavValue.CreateNavValueFromObject(metadata, json.GetString());

            case NavNclType.NavBoolean:
                // BC stores AL Boolean as SQL `tinyint`, so the reader hands back 0 or 1.
                // NavBoolean.CreateFromObject does not accept an integer, so the 0/1 -> bool
                // step is explicit here rather than left to a silent IConvertible coercion.
                if (json.ValueKind == JsonValueKind.True) return NavValue.CreateNavValueFromObject(metadata, true);
                if (json.ValueKind == JsonValueKind.False) return NavValue.CreateNavValueFromObject(metadata, false);
                if (json.ValueKind == JsonValueKind.Number && json.TryGetInt64(out var b) && (b == 0 || b == 1))
                    return NavValue.CreateNavValueFromObject(metadata, b == 1);
                throw new TestDataHydrationRefusal(Refuse(
                    $"expected JSON true/false or the number 0/1, got {json.ValueKind} '{json}'"));

            case NavNclType.NavInteger:
            case NavNclType.NavBigInteger:
            case NavNclType.NavByte:
            case NavNclType.NavOption:
            {
                long value;
                if (json.ValueKind == JsonValueKind.Number && json.TryGetInt64(out value)) { }
                else if (json.ValueKind == JsonValueKind.String
                         && long.TryParse(json.GetString(), System.Globalization.NumberStyles.Integer,
                                          System.Globalization.CultureInfo.InvariantCulture, out value)) { }
                else
                    throw new TestDataHydrationRefusal(Refuse(
                        $"expected an integer, got {json.ValueKind} '{json}'"));
                if (nclType is NavNclType.NavInteger or NavNclType.NavOption or NavNclType.NavByte)
                {
                    if (value < int.MinValue || value > int.MaxValue)
                        throw new TestDataHydrationRefusal(Refuse($"the value {value} does not fit an Integer"));
                    return NavValue.CreateNavValueFromObject(metadata, (int)value);
                }
                return NavValue.CreateNavValueFromObject(metadata, value);
            }

            case NavNclType.NavDecimal:
            {
                // The reader emits decimals as strings, not JSON numbers, precisely so the
                // full SQL scale survives; parse invariantly and never through double.
                decimal value;
                if (json.ValueKind == JsonValueKind.String
                    && decimal.TryParse(json.GetString(), System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out value)) { }
                else if (json.ValueKind == JsonValueKind.Number && json.TryGetDecimal(out value)) { }
                else
                    throw new TestDataHydrationRefusal(Refuse(
                        $"expected a decimal, got {json.ValueKind} '{json}'"));
                return NavValue.CreateNavValueFromObject(metadata, value);
            }

            case NavNclType.NavGuid:
            {
                if (json.ValueKind != JsonValueKind.String || !Guid.TryParse(json.GetString(), out var guid))
                    throw new TestDataHydrationRefusal(Refuse(
                        $"expected a GUID string, got {json.ValueKind} '{json}'"));
                return NavValue.CreateNavValueFromObject(metadata, guid);
            }

            default:
                // Date/DateTime/Time/Duration/DateFormula/Blob/Media/MediaSet/RecordId/…
                // Not "unsupported forever" — unproven. Rebuilding them needs BC's SQL
                // storage encoding for that type (e.g. which SQL datetime value AL's blank
                // date is stored as), and this codec will not assert one that no service tier
                // has confirmed. See .claude/rules/ask-the-corpus-before-claiming-bc-behavior.md.
                throw new TestDataHydrationRefusal(Refuse(
                    "this runner build cannot yet rebuild that AL type from a backup value"));
        }
    }
}
