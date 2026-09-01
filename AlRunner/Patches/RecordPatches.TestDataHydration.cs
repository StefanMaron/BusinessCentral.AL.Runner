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
//
// WHY BLOB / MEDIA / MEDIASET / RECORDID ARE NO LONGER REFUSED (#2270), NOR A DB NULL (#2268)
//   Same source, same method. Media, MediaSet and RecordId transcribe directly; the DB-NULL
//   line above the switch — `(field.NclType == NavNclType.NavBlob) ? new NavBLOB(0)
//   : field.EmptyValue` — is what #2268 asked for, and reaching field.EmptyValue is why this
//   codec is now handed the field's own facts (TestDataFieldFacts) and not only its
//   INavValueMetadata.
//
//   BLOB IS THE ONE THAT IS NOT A LINE-FOR-LINE COPY, and the deviation is deliberate. BC's
//   row SELECT renders a Blob column as DATALENGTH(col), so its reader case builds a
//   LENGTH-ONLY placeholder that a second, lazy query fills in. There is no second query here
//   — this store IS the database — so the transcription target is that second method,
//   NavSqlCommand.GetBlobDataFromReader: BC's four magic bytes plus a raw Deflate stream when
//   the field is Compressed, verbatim bytes when it is not. The NavBlob case below quotes the
//   original and names the measured evidence.
//
//   Duration came with them, once clearing the four above surfaced the first table that
//   actually stores one (Job Queue Entry."Job Timeout" = 43200000, a JSON number of
//   milliseconds).
//
//   Still refused, and named when it is: TableFilter (BC's reader has a case — 504 raw bytes —
//   but no CRONUS table stores one, so the shape the backup reader emits for it has never been
//   measured here, #2271), and any column name that is not an AL field of the target table.
//   Removing nine reasons to refuse did not remove the ability to.
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
//      dropped, counted, and reported.
//
//   c) A column naming a field this runner build's copy of the table does not have is dropped
//      and counted too, under its own name (#2273/#2301). It used to refuse the whole table,
//      on the grounds that a bare unresolvable name could be a schema mismatch. Measured, the
//      refusal was the more dangerous answer: table 309 refused over `Allow Gaps in Nos.` —
//      ObsoleteState = Removed since BC 27.0, so compiled out of the app while both the
//      shipped SymbolReference.json and the physical SQL column survive — and left No. Series
//      Line EMPTY, which is a state AL reads and believes: ~220 of Microsoft's
//      Tests-SINGLESERVER tests failed with "You cannot assign new numbers from the number
//      series <X>" against a backup whose CONT series has 99,977 numbers left.
//      What makes dropping safe is not a guess about why the field is missing: the metatable
//      IS the metadata every AL statement in this run resolves field access against, so a
//      column absent from it is unaddressable here and its absence cannot change an answer AL
//      can read.
//      One refusal remains, for the mismatch the old rule was aimed at: a row shape that
//      shares NO column with the table. Dropping every column of that would insert rows made
//      entirely of defaults.
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

    /// <summary>The outcome of one table's hydration: how many rows landed, how many merged
    /// columns belonged to an app this run does not have installed, and how many named a
    /// field this runner build's copy of the table does not have. All three are reported
    /// rather than left implicit — see the file header, cases (a) and (c).</summary>
    internal readonly record struct TestDataTableResult(
        int Rows, int ColumnsFromUninstalledApps, int ColumnsNotInThisBuild);

    /// <summary>How one table's backup columns divide up against the target metatable.
    /// <see cref="CanHydrate"/> is false only for a row shape that shares NOTHING with the
    /// table — see <see cref="PlanTestDataColumns"/>.</summary>
    internal readonly record struct TestDataColumnPlan(
        IReadOnlyList<string> Mapped,
        IReadOnlyList<string> FromUninstalledApps,
        IReadOnlyList<string> NotInThisBuild)
    {
        internal bool CanHydrate => Mapped.Count > 0 || (FromUninstalledApps.Count == 0 && NotInThisBuild.Count == 0);
    }

    /// <summary>
    /// Sort one table's backup columns into the three things a column can be.
    ///
    /// A column that is not a field of the target metatable is DROPPED, not a reason to
    /// refuse the table. The reason it is safe is narrow and load-bearing: the metatable is
    /// the very metadata every AL statement in this run resolves field access against, so a
    /// column missing from it is not addressable by any AL code here — dropping it cannot
    /// change an answer AL can read. Refusing instead leaves the table EMPTY, which AL very
    /// much can read, and reads as "this table has no rows" rather than as a diagnostic.
    /// Measured: table 309 refused over `Allow Gaps in Nos.` (ObsoleteState = Removed since
    /// BC 27.0, still declared in the shipped symbols and still a physical SQL column), and
    /// ~220 of Microsoft's Tests-SINGLESERVER tests then failed on number series that have
    /// 99,977 numbers left in the backup.
    ///
    /// Two counts, not one, because they mean different things: a `&lt;sql&gt;$&lt;app id&gt;` column
    /// says an app is outside this run's closure (case (a)), while a bare name says this
    /// build's table has no such field (case (c)).
    ///
    /// The one refusal left is a row shape that shares NO column with the table: that is a
    /// mismatch, and hydrating it would insert rows made entirely of defaults.
    /// </summary>
    internal static TestDataColumnPlan PlanTestDataColumns(
        IReadOnlySet<string> fieldNames, IEnumerable<string> columnNames)
    {
        var mapped = new List<string>();
        var fromUninstalledApps = new List<string>();
        var notInThisBuild = new List<string>();
        foreach (var name in columnNames.Distinct(StringComparer.Ordinal))
        {
            if (fieldNames.Contains(name)) mapped.Add(name);
            else if (BackupCatalog.TryParseUnresolvedExtensionColumn(name, out _, out _)) fromUninstalledApps.Add(name);
            else notInThisBuild.Add(name);
        }
        return new TestDataColumnPlan(mapped, fromUninstalledApps, notInThisBuild);
    }

    /// <summary>
    /// Everything BC's own SQL-cell reader reads off the field it is converting for. There are
    /// exactly three things, and the last two are the reason this type exists at all:
    /// <see cref="NCLMetaField.EmptyValue"/> (BC's answer for a DB NULL in ANY column type) and
    /// <see cref="NCLMetaField.FieldIsCompressed"/> (whether a Blob column's stored bytes are
    /// BC's compressed container). Neither is on <see cref="INavValueMetadata"/>, which is all
    /// the codec used to be handed.
    ///
    /// It is a struct over an NCLMetaField rather than the NCLMetaField itself so the codec can
    /// be exercised without a booted engine: BC constructs NCLMetaField only from a MetaField
    /// plus a parent NCLMetaTable, and the conversion under test is pure over (these facts, the
    /// JSON cell). <see cref="EmptyValue"/> is a delegate, not a value, because reading it is
    /// only correct in the NULL branch — for a Blob or an enum Option it ALLOCATES on every
    /// read, and the overwhelming majority of cells are not NULL.
    /// </summary>
    internal readonly struct TestDataFieldFacts
    {
        internal TestDataFieldFacts(INavValueMetadata metadata, Func<NavValue> emptyValue, bool storedCompressed)
        {
            Metadata = metadata;
            EmptyValue = emptyValue;
            StoredCompressed = storedCompressed;
        }

        internal INavValueMetadata Metadata { get; }

        /// <summary>NCLMetaField.EmptyValue.</summary>
        internal Func<NavValue> EmptyValue { get; }

        /// <summary>NCLMetaField.FieldIsCompressed — set from the AL field's `Compressed`
        /// property. Only meaningful for a Blob column.</summary>
        internal bool StoredCompressed { get; }

        internal NavNclType NclType => Metadata.NclType;

        internal static TestDataFieldFacts For(NCLMetaField field)
            => new(field, () => field.EmptyValue, field.FieldIsCompressed);
    }

    /// <summary>
    /// Insert <paramref name="rows"/> into <paramref name="tableId"/>'s in-memory store.
    /// <paramref name="rows"/> is one dictionary per row, keyed by the AL field NAME the
    /// reader emitted (BC's system columns already dropped by the caller).
    ///
    /// A key that resolves to a field of the target NCLMetaTable — the metatable the row is
    /// actually inserted into — is hydrated. A key that does not is dropped and counted, in
    /// one of two buckets: a table-extension storage column (`&lt;sql name&gt;$&lt;app id&gt;`) owned by
    /// an app outside this run's closure, or a name this build's copy of the table has no
    /// field for. See the file header, cases (a) and (c), for why dropping is the faithful
    /// answer and refusing was not.
    ///
    /// Throws <see cref="TestDataHydrationRefusal"/> BEFORE touching the store if any value
    /// cannot be rebuilt, so a refusal never leaves rows behind.
    /// </summary>
    internal static TestDataTableResult HydrateTestDataTable(
        int tableId, string tableNameForDiagnostics,
        IReadOnlyList<IReadOnlyDictionary<string, JsonElement>> rows)
        => HydrateTestDataTable(tableId, tableNameForDiagnostics, rows, null, out _, out _);

    /// <summary>
    /// The overload the on-demand loader (#2262) uses. Two additions, both about WHERE the
    /// rows go rather than how they are built:
    ///
    /// <paramref name="intoSource"/> — the DataAccessSource to insert into. Null keeps the
    /// eager path's behaviour (the skeleton session's source). The lazy path passes the very
    /// source GetDataAccessForTableCore was called on, so the rows land in the storage that
    /// call is about to hand back rather than in a different source's copy of the table.
    ///
    /// <paramref name="pristineRows"/> — the rows as built, BEFORE any AL code can touch
    /// them. The lazy caller needs them because a load fired mid-test happens long after
    /// CaptureInstallBaselineSnapshot() walked the store, so the rows have to be handed to
    /// AppendBaselineTable explicitly or the next boundary drops them. Handing back the live
    /// arrays would let a test's mutation reach the baseline; AppendBaselineTable deep-copies
    /// on the way in, which is the same discipline as the capture path.
    /// </summary>
    internal static TestDataTableResult HydrateTestDataTable(
        int tableId, string tableNameForDiagnostics,
        IReadOnlyList<IReadOnlyDictionary<string, JsonElement>> rows,
        object? intoSource,
        out NCLMetaTable? metaTable, out NavValue[][] pristineRows)
    {
        metaTable = null;
        pristineRows = Array.Empty<NavValue[]>();
        if (rows.Count == 0) return new TestDataTableResult(0, 0, 0);

        var meta = EnsureTableInMetadataCache(tableId)
            ?? throw new TestDataHydrationRefusal(
                $"table {tableId} '{tableNameForDiagnostics}': this process has no NCLMetaTable for it, "
                + "so its rows cannot be turned into AL records");

        var source = intoSource ?? ResolveSkeletonDataAccessSource()
            ?? throw new TestDataHydrationRefusal(
                $"table {tableId} '{tableNameForDiagnostics}': the skeleton session has no DataAccessSource yet");

        var fieldByName = new Dictionary<string, NCLMetaField>(StringComparer.Ordinal);
        for (var fi = 0; fi < meta.FieldCount; fi++)
        {
            var f = meta.GetFieldByIndex(fi);
            fieldByName[f.FieldName] = f;
        }

        // Neither dropped list is looked up again: BuildTestDataRow indexes each row by the
        // metatable's own field names, so a dropped column is simply never asked for.
        var plan = PlanTestDataColumns(
            (IReadOnlySet<string>)new HashSet<string>(fieldByName.Keys, StringComparer.Ordinal),
            rows.SelectMany(r => r.Keys));
        if (!plan.CanHydrate)
            throw new TestDataHydrationRefusal(
                $"table {tableId} '{tableNameForDiagnostics}': not one of the backup's "
                + $"{plan.FromUninstalledApps.Count + plan.NotInThisBuild.Count} column(s) is a field of the AL "
                + $"table this runner build would insert into ({string.Join(", ", plan.NotInThisBuild.Concat(plan.FromUninstalledApps).Take(5))}"
                + "…), so these rows are not this table's rows");

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
        // Handed back only AFTER every row is in the store, so a caller can never see rows
        // for a table that ended up refused.
        metaTable = meta;
        pristineRows = built;
        return new TestDataTableResult(
            built.Length, plan.FromUninstalledApps.Count, plan.NotInThisBuild.Count);
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
                TestDataFieldFacts.For(field), json, tableId, tableName, field.FieldNo, field.FieldName);
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
        TestDataFieldFacts field, JsonElement json, int tableId, string tableName, int fieldNo, string columnName)
    {
        var metadata = field.Metadata;
        string Refuse(string why) =>
            $"table {tableId} '{tableName}', column '{columnName}' (AL field {fieldNo}, {metadata.NclType}): {why}";

        var nclType = metadata.NclType;

        if (json.ValueKind == JsonValueKind.Null)
        {
            // A DB NULL, and BC's reader is the whole answer (#2268):
            //   if (reader.IsDBNull(columnIndex))
            //       return (field.NclType == NavNclType.NavBlob) ? new NavBLOB(0) : field.EmptyValue;
            //
            // #2258 refused every non-string NULL on the grounds that "only Text/Code have a
            // NavValue that can represent one". That was untrue of BC, which has an answer for
            // a NULL in any column type, and it cost 11 of the 41 tables still refusing —
            // every one of them over a NULL Blob, `Sales Header` among them.
            //
            // Note this is NOT a NULL-preserving conversion, in either arm, and it is not
            // meant to be. `new NavBLOB(0)` is an empty, not-in-memory blob, and EmptyValue for
            // a Text is NavText.Default(len) — an EMPTY string, not a null one. So BC's own
            // record buffers never carry a null NavText read out of SQL, and neither do ours
            // now. The two are indistinguishable from AL (both Value "", both compare equal,
            // both Format to ''); they differ only in NavValue.IsNull, which nothing on the AL
            // side reads.
            return nclType == NavNclType.NavBlob ? new NavBLOB(0) : field.EmptyValue();
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

            // The four types below are #2270, and they are transcribed from the same method
            // as the date/time ones — with ONE structural difference that has to be said out
            // loud, because it is the only place this codec deliberately does not copy
            // CreateNavValueFromReader line for line.

            case NavNclType.NavBlob:
            {
                // BC's row SELECT does not fetch a Blob's bytes at all. It renders the column
                // as DATALENGTH(col) (NavSqlStatementHelper.AppendFieldList, column list type
                // DataLengthInsteadOfBlobs), so its reader case is
                //     case NavNclType.NavBlob: return new NavBLOB(reader.GetInt32(i));
                // — a placeholder carrying a LENGTH and no content, which BC fills in later,
                // lazily, from a second query. There is no second query here: this store IS
                // the database, and a length-only blob would read back as empty in AL.
                //
                // So the transcription target is the method that does the filling,
                // NavSqlCommand.GetBlobDataFromReader:
                //     if (isCompressed) {
                //         byte[] magic = new byte[4];
                //         if (blobReaderStream.Read(magic, 0, 4) < 4 || !NavBLOB.BlobMagicOk(magic))
                //             return 22926086;
                //         using DeflateStream d = new(blobReaderStream, CompressionMode.Decompress);
                //         d.CopyTo(stream, num - 4);
                //     } else blobReaderStream.CopyTo(stream);
                // with isCompressed = blobField.FieldIsCompressed. Measured on the shipped
                // CRONUS backup: Company Information."Picture" is 12,921 stored bytes that
                // deflate to a 15,225-byte JPEG, and Retention Policy Setup Line."Table
                // Filter" to `VERSION(1) SORTING(Field1) WHERE(Field25=1(1))`.
                var stored = ParseTestDataHexBytes(json, Refuse);
                if (stored.Length == 0) return NavBLOB.Default();
                return new NavBLOB(DecodeTestDataBlobBytes(stored, field.StoredCompressed, Refuse));
            }

            case NavNclType.NavMedia:
            case NavNclType.NavMediaSet:
            {
                // BC:
                //   case NavNclType.NavMedia:    return new NavMedia(reader.GetGuid(i), parentTableId);
                //   case NavNclType.NavMediaSet: return new NavMediaSet(reader.GetGuid(i), parentTableId);
                //
                // The stored cell is the media (or media-set) id and nothing else — measured,
                // the reader hands back "57C8E273-1769-4173-AAED-0A56E3ADCB8D" for Word
                // Template "EVENT".Template. The BYTES behind that id live in Tenant Media,
                // which is a table like any other; nothing here fabricates them.
                //
                // parentTableId is -1 because that is what BC's own row read passes: its
                // ReaderToRecord calls the three-argument overload, whose default is -1, and
                // NavRecord.GetFieldValue then COPIES the value and calls
                // SetOwnerRecordInformation with the real table id before AL ever sees it. So
                // the id stored in the buffer is never the one read.
                if (json.ValueKind != JsonValueKind.String || !Guid.TryParse(json.GetString(), out var mediaId))
                    throw new TestDataHydrationRefusal(Refuse(
                        $"expected a media id GUID string, got {json.ValueKind} '{json}'"));
                return CreateOrRefuse(
                    () => nclType == NavNclType.NavMedia
                        ? new NavMedia(mediaId, parentId: -1)
                        : new NavMediaSet(mediaId, parentId: -1),
                    Refuse);
            }

            case NavNclType.NavDuration:
            {
                // BC:
                //   case NavNclType.NavDuration: return new NavDuration(reader.GetInt64(i));
                //
                // A SQL `bigint` of milliseconds, and the reader emits it as a JSON number —
                // measured on Job Queue Entry."Job Timeout", 43200000 (twelve hours). Kept
                // separate from the Integer/BigInteger case above rather than folded into it:
                // that case ends at NavValue.CreateNavValueFromObject, whose NavDuration arm
                // is NavDuration.CreateFromObject, and this is the constructor BC's own reader
                // calls.
                if (json.ValueKind != JsonValueKind.Number || !json.TryGetInt64(out var ms))
                    throw new TestDataHydrationRefusal(Refuse(
                        $"expected a duration in whole milliseconds, got {json.ValueKind} '{json}'"));
                return CreateOrRefuse(() => new NavDuration(ms), Refuse);
            }

            case NavNclType.NavRecordId:
            {
                // BC:
                //   byte[] a = new byte[448];
                //   reader.GetBytes(columnIndex, 0L, a, 0, a.Length);
                //   return new NavRecordId(a);
                //
                // SqlDataReader.GetBytes copies min(stored, 448) and leaves the rest zero, so
                // a short cell is legal and the buffer is what makes it so — measured, both
                // RecordId columns in the shipped CRONUS backup hold six zero bytes. The
                // buffer is reproduced rather than passing the stored bytes straight through
                // because NavRecordId's parse walks to a uint16 terminator, and trailing zeros
                // are what terminates it.
                var stored = ParseTestDataHexBytes(json, Refuse);
                if (stored.Length > NavRecordId.MaxByteSize)
                    throw new TestDataHydrationRefusal(Refuse(
                        $"the backup holds {stored.Length} bytes, more than the "
                        + $"{NavRecordId.MaxByteSize} BC reads into a RecordId"));
                var buffer = new byte[NavRecordId.MaxByteSize];
                Array.Copy(stored, buffer, stored.Length);
                return CreateOrRefuse(() => new NavRecordId(buffer), Refuse);
            }

            default:
                // TableFilter/…
                // Not "unsupported forever" — unproven. BC's reader has a TableFilter case
                // (504 raw bytes), but no table in the shipped CRONUS data stores one, so the
                // shape the backup reader emits for it has never been measured here and this
                // codec will not invent one. See #2271.
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

    /// <summary>BC's own <c>NavBLOB.BlobMagic</c>, transcribed. A Blob column whose field is
    /// Compressed stores these four bytes followed by a RAW Deflate stream — that is what
    /// <c>NavBLOB.GetSqlWritableValue(compressed: true)</c> writes and what
    /// <c>NavSqlCommand.GetBlobDataFromReader</c> reads back.</summary>
    private static readonly byte[] TestDataBlobMagic = { 0x02, 0x45, 0x7D, 0x5B };

    /// <summary>
    /// The one shape the reader emits for every binary column — a JSON string
    /// <c>0x&lt;hex&gt;</c>, measured across the Blob, Media-less binary and RecordId cells it
    /// produced for CRONUS from <c>sandbox/28.1.49838.50621/w1/BusinessCentral-W1.bak</c>.
    /// Anything else refuses: a decoding change this codec silently reinterpreted would put
    /// wrong bytes in the store with nothing to notice it.
    /// </summary>
    private static byte[] ParseTestDataHexBytes(JsonElement json, Func<string, string> refuse)
    {
        if (json.ValueKind != JsonValueKind.String)
            throw new TestDataHydrationRefusal(refuse(
                $"expected a 0x-prefixed hex string, got {json.ValueKind} '{json}'"));
        var text = json.GetString() ?? string.Empty;
        if (!text.StartsWith("0x", StringComparison.Ordinal))
            throw new TestDataHydrationRefusal(refuse(
                $"'{text}' is not the 0x-prefixed hex string the reader is known to emit for a "
                + "binary column"));
        var hex = text.AsSpan(2);
        if (hex.Length % 2 != 0)
            throw new TestDataHydrationRefusal(refuse(
                $"'{text}' has an odd number of hex digits, so it cannot be a byte sequence"));
        try { return System.Convert.FromHexString(hex); }
        catch (FormatException)
        {
            throw new TestDataHydrationRefusal(refuse($"'{text}' is not valid hexadecimal"));
        }
    }

    /// <summary>
    /// <c>NavSqlCommand.GetBlobDataFromReader</c>, transcribed: unwrap BC's compressed
    /// container when the field says it is compressed, and take the bytes verbatim when it
    /// does not.
    ///
    /// Both mismatches refuse rather than fall back to the other branch, and that is the whole
    /// point of the method. BC's read path returns error 22926086 when a compressed field's
    /// bytes do not start with the magic — it does not shrug and treat them as content. The
    /// mirror case (the field says uncompressed, the bytes carry the container) has no BC
    /// precedent because BC's write path cannot produce it; it means this build's metadata and
    /// the backup disagree about the field, and storing the container as if it were the value
    /// would be the silent-wrong outcome .claude/rules/loud-failures.md exists to stop.
    /// </summary>
    private static byte[] DecodeTestDataBlobBytes(byte[] stored, bool compressed, Func<string, string> refuse)
    {
        var carriesContainer = stored.Length >= TestDataBlobMagic.Length
            && stored.AsSpan(0, TestDataBlobMagic.Length).SequenceEqual(TestDataBlobMagic);

        if (!compressed)
        {
            if (carriesContainer)
                throw new TestDataHydrationRefusal(refuse(
                    "this build's field metadata says the column is not compressed, but the "
                    + "backup's bytes start with BC's compressed-BLOB marker — one of the two is "
                    + "wrong about the field, and storing the container as the value would be a "
                    + "silently wrong blob"));
            return stored;
        }

        if (!carriesContainer)
            throw new TestDataHydrationRefusal(refuse(
                $"this build's field metadata says the column is compressed, but its {stored.Length} "
                + "stored byte(s) do not start with BC's compressed-BLOB marker (BC's own reader "
                + "fails with 22926086 here rather than reading them as content)"));

        try
        {
            using var source = new MemoryStream(stored, TestDataBlobMagic.Length,
                stored.Length - TestDataBlobMagic.Length, writable: false);
            using var deflate = new System.IO.Compression.DeflateStream(
                source, System.IO.Compression.CompressionMode.Decompress);
            using var content = new MemoryStream();
            deflate.CopyTo(content);
            return content.ToArray();
        }
        catch (InvalidDataException ex)
        {
            throw new TestDataHydrationRefusal(refuse(
                $"the column's stored bytes carry BC's compressed-BLOB marker but are not a "
                + $"Deflate stream ({ex.Message})"));
        }
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
