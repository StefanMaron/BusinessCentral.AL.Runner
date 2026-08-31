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
//   Values are rebuilt through BC's OWN code. The scalar types go through
//   NavValue.CreateNavValueFromObject handed the target field's metadata; the date/time-shaped
//   types mirror, case for case, BC's own SQL-cell reader
//   (NavSqlCommand.CreateNavValueFromReader in Microsoft.Dynamics.Nav.Ncl.dll). Any value this
//   file cannot prove it rebuilds identically aborts THAT TABLE's hydration with a message
//   naming the table, the column and the type — it never substitutes a default and never leaves
//   a partially-built row in the store.
//
// WHY THE DATE/TIME TYPES ARE NO LONGER REFUSED (issue #2259)
//   #2258 refused Date, DateTime, Time and DateFormula because BC's SQL storage encoding for
//   them was not established. It is now, from BC's own assemblies rather than from reasoning:
//     - The blank marker is 1753-01-01, named per type — NavDate.SqlDateTimeUndefined (Local),
//       NavDateTime.SqlDateTimeUtcUndefined (Utc), NavTime.SqlTimeUndefined (Local). The two
//       kinds are NOT interchangeable and are not normalised to one here.
//     - It is unambiguous, because NavDate.GetSqlWritableValue THROWS for any real date below
//       1754-01-01 rather than storing one. A column holding both 1753-01-01 and real dates is
//       holding blanks and real dates, not two meanings of one value.
//     - DateFormula is stored as BC's TOKEN encoding (measured: Payment Terms."Due Date
//       Calculation" for "10 DAYS" is "10" + U+0002, not "10D"), which is what the
//       isTokenString: true constructor consumes.
//   Evidence: Microsoft.Dynamics.Nav.Ncl.dll from sandbox/28.1.49838.50621 and
//   sandbox/27.5.46862.48827, decompiled — the read path is identical in both.
//
//   Still refused, and named when they are: Blob, Media, MediaSet (#2245), RecordId, Duration,
//   TableFilter, a DB NULL in a non-string column (#2268 — BC's reader answers that one too,
//   but it needs the NCLMetaField this method is not handed), and any column name that is not
//   an AL field of the target table. Removing four reasons to refuse did not remove the
//   ability to: measured on the shipped CRONUS backup, 41 tables still decline.
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
//   b) The merge can fail to happen at all without failing. Measured on reader builds up to
//      9701b04: `--mergeExtensions` (camelCase) was accepted by the CLI, ignored, and exited
//      0 — which would hydrate `Source Code Setup` with its ONE own field, ~50 blanks, and no
//      error anywhere. Reader a431ee4 refuses an unaccepted option (BakReader#18), but the
//      runner pins no reader version and cannot control the binary on a user's PATH, so the
//      guard stays. That guard is NOT here, deliberately: this metatable cannot answer "is this
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
using Microsoft.Dynamics.Nav.Types.Exceptions;

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
    /// This method never invents an encoding. The scalar branches hand a CLR object to BC's own
    /// <see cref="NavValue.CreateNavValueFromObject"/>; the Date/DateTime/Time/DateFormula
    /// branches transcribe BC's own SQL reader, <c>NavSqlCommand.CreateNavValueFromReader</c>,
    /// quoted inline at each case. A type that is not listed is refused, by design.
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
            // A DB NULL. Only the string-like types are rebuilt here (the same restriction
            // BC's own byte codec has — see RecordPatches.InstallBaselineDisk's KindNullString).
            // BC's SQL READER does have an answer for every type — field.EmptyValue, and
            // new NavBLOB(0) for a Blob — but reaching it needs the NCLMetaField this method is
            // not handed. #2268 tracks it; measured, it is 11 of the 41 remaining refusals.
            // Until then this refuses, which is the safe direction.
            if (!isStringLike)
                throw new TestDataHydrationRefusal(Refuse(
                    "the backup holds a NULL, and this runner build only rebuilds NULLs for "
                    + "Text/Code (see issue #2268 — BC's own SQL reader answers a NULL for every "
                    + "type, but doing the same here needs metadata this codec is not handed)"));
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

            // The four date/time-shaped types below mirror, case for case, BC's OWN
            // SQL-cell-to-NavValue conversion:
            // Microsoft.Dynamics.Nav.Runtime.NavSqlCommand.CreateNavValueFromReader(
            //     SqlDataReader, INavFieldMetadata, int, int)
            // in Microsoft.Dynamics.Nav.Ncl.dll — identical in the 27.5 and 28.1 artifacts.
            // Anything that deviates from that method is a bug here, not a design choice.

            case NavNclType.NavTime:
            {
                // BC:
                //   DateTime dt = reader.GetDateTime(i);
                //   if (dt.Equals(NavTime.SqlTimeUndefined)) return NavTime.Undefined;
                //   return NavTime.Create(dt.Hour, dt.Minute, dt.Second, dt.Millisecond);
                //
                // NavTime has its OWN sentinel. It is the same instant as NavDate's, but a
                // blank Time and 00:00:00 are different AL values, so the check is what keeps
                // them apart — real midnight is stored on the 1754-01-01 carrier day.
                // DateTime.Equals compares ticks and ignores Kind, which is why comparing an
                // Unspecified-kind parse against a Local-kind constant is BC's own shape too
                // (SqlDataReader.GetDateTime also returns Unspecified).
                var cell = ParseTestDataSqlDateTime(json, Refuse);
                if (cell.Equals(NavTime.SqlTimeUndefined)) return NavTime.Undefined;
                // Only the time-of-day is part of the AL value; the date half is SQL's carrier
                // for a `datetime` column and BC discards it.
                return CreateOrRefuse(
                    () => NavTime.Create(cell.Hour, cell.Minute, cell.Second, cell.Millisecond), Refuse);
            }

            case NavNclType.NavDate:
            {
                // BC:
                //   DateTime v = new DateTime(reader.GetDateTime(i).Ticks, DateTimeKind.Local);
                //   if (v.Equals(NavDate.SqlDateTimeUndefined)) return NavDate.Undefined;
                //   return NavDate.Create(v);
                //
                // The sentinel is 1753-01-01, and it is unambiguous: NavDate.GetSqlWritableValue
                // writes it for a blank date and THROWS (NavCSideException 22928068) for any
                // real date outside [1754-01-01, 9999-12-31 23:59:59.997], so no real AL date
                // can collide with it. Kind must be Local — NavDate's constructor rejects
                // anything else outright.
                var cell = ParseTestDataSqlDateTime(json, Refuse);
                var value = new DateTime(cell.Ticks, DateTimeKind.Local);
                if (value.Equals(NavDate.SqlDateTimeUndefined)) return NavDate.Undefined;
                RefuseIfOutsideBcsWritableSqlRange(value, Refuse);
                return CreateOrRefuse(() => NavDate.Create(value), Refuse);
            }

            case NavNclType.NavDateTime:
            {
                // BC:
                //   DateTime v = DateTime.SpecifyKind(reader.GetDateTime(i), DateTimeKind.Utc);
                //   if (v.Equals(NavDateTime.SqlDateTimeUtcUndefined)) return NavDateTime.Undefined;
                //   return NavDateTime.Create(null, v, DateTimeReferenceFrame.Server,
                //                             unspecifiedAsLocal: false);
                //
                // NOT CreateFromObject. That overload builds with DateTimeReferenceFrame.Client
                // and unspecifiedAsLocal: true, which sends the value through ConvertToUTc and
                // shifts it by the machine's UTC offset. Specifying Utc kind takes the verbatim
                // branch of NavDateTime's constructor instead, which touches neither a session
                // nor a time zone — that is why a null session is safe here.
                var cell = ParseTestDataSqlDateTime(json, Refuse);
                var value = DateTime.SpecifyKind(cell, DateTimeKind.Utc);
                if (value.Equals(NavDateTime.SqlDateTimeUtcUndefined)) return NavDateTime.Undefined;
                if (value < NavDateTime.SqlDateTimeUtcFirstValid
                    || value > NavDateTime.SqlDateTimeUtcLastValid)
                    throw new TestDataHydrationRefusal(Refuse(
                        $"the backup holds '{value:yyyy-MM-dd HH:mm:ss.fff}', which is outside the range BC "
                        + "will write to a SQL datetime column and is not its blank sentinel either, so the "
                        + "reader decoded something BC cannot have stored"));
                return CreateOrRefuse(
                    () => NavDateTime.Create(
                        (NavSession?)null, value, DateTimeReferenceFrame.Server, unspecifiedAsLocal: false),
                    Refuse);
            }

            case NavNclType.NavDateFormula:
            {
                // BC:
                //   string text = reader.GetString(i);
                //   if (string.IsNullOrEmpty(text)) return field.EmptyValue;
                //   return new NavDateFormula(text, isTokenString: true);
                //
                // The stored value is BC's TOKEN encoding, not readable formula text: measured,
                // Payment Terms."Due Date Calculation" for "10 DAYS" is "10" + U+0002, and
                // Company Information."Cal. Convergence Time Frame" is "1" + U+0007. The
                // single-argument NavDateFormula(string) overload would run that through
                // NavDateFormulaEvaluator.Parse as formula TEXT and produce a different value;
                // isTokenString: true is what consumes it as tokens.
                if (json.ValueKind != JsonValueKind.String)
                    throw new TestDataHydrationRefusal(Refuse(
                        $"expected a DateFormula token string, got {json.ValueKind} '{json}'"));
                var text = json.GetString() ?? string.Empty;
                // NavDateFormula.Default is the same instance BC's field.EmptyValue resolves to
                // for a DateFormula field (NCLMetaField.EmptyValue -> NavValue.GetDefaultNavValue
                // -> NavDateFormula.Default), so this is that branch, not an approximation of it.
                if (text.Length == 0) return NavDateFormula.Default;
                return CreateOrRefuse(() => new NavDateFormula(text, isTokenString: true), Refuse);
            }

            default:
                // Duration/Blob/Media/MediaSet/RecordId/TableFilter/…
                // Not "unsupported forever" — unproven. Rebuilding them needs BC's SQL
                // storage encoding for that type, and this codec will not assert one it has
                // not established. LOB/binary is tracked in #2245.
                throw new TestDataHydrationRefusal(Refuse(
                    "this runner build cannot yet rebuild that AL type from a backup value"));
        }
    }

    /// <summary>NavDate's own private <c>SqlDateTimeFirstValid</c>, transcribed. BC's write path
    /// throws rather than store a Date below it, which is what makes 1753-01-01 an unambiguous
    /// blank marker rather than one of two meanings sharing a column.</summary>
    private static readonly DateTime TestDataSqlDateFirstValid =
        new(1754, 1, 1, 0, 0, 0, 0, DateTimeKind.Local);

    /// <summary>
    /// The one shape the reader emits for every SQL <c>datetime</c> column, whatever the AL type
    /// on top of it: a JSON string <c>yyyy-MM-dd HH:mm:ss.fff</c>. Measured across all 4,854
    /// Date, 503 DateTime and 302 Time cells the reader produced for CRONUS from
    /// <c>sandbox/28.1.49838.50621/w1/BusinessCentral-W1.bak</c> — one shape, no exceptions.
    ///
    /// The three variants after it are not measured; they are the unambiguous renderings a
    /// reader change could plausibly move to (whole seconds, <c>datetime2</c> precision, an ISO
    /// separator). Anything else refuses, which is the point: a decoding change that this codec
    /// silently reinterpreted would put wrong dates in the store with nothing to notice it.
    /// </summary>
    private static readonly string[] TestDataSqlDateTimeFormats =
    {
        "yyyy-MM-dd HH:mm:ss.fff",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.fffffff",
        "yyyy-MM-ddTHH:mm:ss.fff",
    };

    private static DateTime ParseTestDataSqlDateTime(JsonElement json, Func<string, string> refuse)
    {
        if (json.ValueKind != JsonValueKind.String)
            throw new TestDataHydrationRefusal(refuse(
                $"expected a SQL datetime string, got {json.ValueKind} '{json}'"));

        // DateTimeStyles.None yields DateTimeKind.Unspecified — the same kind
        // SqlDataReader.GetDateTime hands BC, so each branch above applies the kind BC applies
        // rather than inheriting one from the parse.
        if (!DateTime.TryParseExact(json.GetString(), TestDataSqlDateTimeFormats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
            throw new TestDataHydrationRefusal(refuse(
                $"'{json.GetString()}' is not a SQL datetime the reader is known to emit "
                + $"(expected {TestDataSqlDateTimeFormats[0]})"));
        return parsed;
    }

    /// <summary>
    /// Refuse a Date outside the window BC's own write path will store. BC's READ path does not
    /// range-check, because SQL already held it to that window on the way in; here the value
    /// arrived through a third-party backup decoder instead, so the guarantee has to be
    /// re-checked. A date in 1753 that is not the sentinel cannot have been written by BC, so it
    /// means the decode was wrong — and a plausible-looking wrong date in the store is exactly
    /// what .claude/rules/loud-failures.md exists to stop.
    /// </summary>
    private static void RefuseIfOutsideBcsWritableSqlRange(DateTime value, Func<string, string> refuse)
    {
        if (value >= TestDataSqlDateFirstValid) return;
        throw new TestDataHydrationRefusal(refuse(
            $"the backup holds '{value:yyyy-MM-dd HH:mm:ss.fff}', which is earlier than the "
            + $"{TestDataSqlDateFirstValid:yyyy-MM-dd} floor BC will write to a SQL datetime column and is "
            + "not its blank sentinel either, so the reader decoded something BC cannot have stored"));
    }

    /// <summary>
    /// Run one of BC's own NavValue constructors and turn its rejection into a per-table refusal.
    /// Without this a NavNCLException would escape past TestDataProvisioner's catch and abort the
    /// whole run, where the contract is that ONE table declines and the rest still hydrate.
    /// </summary>
    private static NavValue CreateOrRefuse(Func<NavValue> create, Func<string, string> refuse)
    {
        try { return create(); }
        catch (NavNCLException ex)
        {
            throw new TestDataHydrationRefusal(refuse(
                $"BC's own value constructor rejected the decoded cell ({ex.GetType().Name}: {ex.Message})"));
        }
    }
}
