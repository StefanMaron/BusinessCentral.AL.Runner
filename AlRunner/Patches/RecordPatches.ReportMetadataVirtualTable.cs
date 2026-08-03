// RecordPatches.ReportMetadataVirtualTable — managed providers for the
// "Report Metadata" (2000000139) and "Report Data Items" (2000000203) system
// virtual tables.
//
// WHY THIS EXISTS
//   These two tables ARE the documented, supported way for AL to discover a
//   report's shape without running it: its caption, whether it has a request
//   page, whether it is processing-only, and what its dataset looks like.
//   Both are virtual on the service tier (their rows are computed from the
//   metadata of every published report), and both routed to the same empty
//   in-memory store as every other table here. So:
//
//     Report Metadata.Get(<any id>)  -> false, always
//     Report Data Items              -> empty, always
//
//   That is a silent wrong answer, not an error, so AL takes its not-found
//   branch and reports something misleading one level up. Pageworks' report
//   discovery entity set binds to Report Metadata and filters on
//   FirstDataItemTableID <> 0, so it lists nothing at all; its DeriveSourceTable
//   reads FirstDataItemTableID and falls back to Report Data Items, so it
//   answers 0 for Base Application report 1306 whose root data item is plainly
//   Sales Invoice Header (112).
//
// WHERE THE ROWS COME FROM (two sources, neither invented)
//   1. Reports the runner compiles itself — parsed from their AL source
//      (RecordPatches.AlReportParser.cs: Caption / UseRequestPage /
//      ProcessingOnly and the real brace-nested data-item tree).
//   2. Reports living in a PRECOMPILED dependency (Base Application, System
//      Application, ISV apps) — read from that .app's SymbolReference.json,
//      which carries Id, Name, the Caption property and the full DataItems
//      tree with per-item RelatedTable and Indentation
//      (BcAppSymbolCache.TryParseReportSymbol). This is the only route for an
//      R2R app: it ships no metadata XML, and parsing its 8000-file embedded
//      src/ for this would be absurd.
//   Source-compiled reports win over symbol-derived ones for the same id — the
//   source is what this run actually compiled.
//
// NO SILENT WRONG ANSWERS
//   FirstDataItemTableID = 0 is meaningful: it is how a caller recognizes a
//   processing-only report. So a report whose root data item exists but whose
//   TABLE the runner cannot resolve must not be handed out as 0 — that would
//   claim "no dataset" about a report that plainly has one. Such a report is
//   omitted from the table entirely (Get returns false, i.e. "the runner does
//   not know this report", which is true) and counted in a single stderr line
//   naming the reports affected.
//
// PRECOMPILED-DLL RESPECT
//   Runtime-engine types only (VirtualDataProvider, NCLMetaTable, NavValue,
//   ReadOnlyRecordBuffer, TempTableDataProvider), reached through the same
//   helpers the AllObj provider resolves. No AL business-logic body is touched.

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int ReportMetadataVirtualTableId = 2000000139;
    internal const int ReportDataItemsVirtualTableId = 2000000203;

    private static readonly ConditionalWeakTable<object, ConcurrentDictionary<int, byte>> _rmvPopulatedByProvider = new();
    private static readonly ConditionalWeakTable<object, ConcurrentDictionary<(int, int), byte>> _rdiPopulatedByProvider = new();

    private static bool IsReportMetadataVirtualTable(NCLMetaTable? table)
        => table != null && table.TableId == ReportMetadataVirtualTableId;

    private static bool IsReportDataItemsVirtualTable(NCLMetaTable? table)
        => table != null && table.TableId == ReportDataItemsVirtualTableId;

    /// <summary>
    /// One report as the two virtual tables expose it, merged from whichever source
    /// knows about it. <see cref="FirstDataItemTableId"/> is already resolved to a real
    /// table id (0 only when the report genuinely declares no data item).
    /// </summary>
    private sealed record ReportRow(
        int Id, string Name, string Caption, bool ProcessingOnly, bool UseRequestPage,
        string WordMergeDataItem, int FirstDataItemTableId,
        List<ReportDataItemRow> DataItems);

    private sealed record ReportDataItemRow(
        int Id, string Name, int RelatedTableId, int Indentation,
        string DataItemTableView, string RequestFilterFields);

    // Cached inventory both tables read. Rebuilt whenever the runner has learned about
    // more source-parsed reports or registered another dependency .app since the last
    // build — the FIRST handout of either table can happen before the bundle's
    // dependencies are registered, and a snapshot taken then would permanently hide
    // every Base Application report from both tables (which is exactly what a
    // build-once cache did: report 1306 stayed invisible while the bundle's own
    // reports resolved fine).
    private static List<ReportRow>? _reportRows;
    private static (int Apps, int Parsed) _reportRowsBuiltFrom = (-1, -1);
    private static readonly object _reportRowsLock = new();

    /// <summary>
    /// Populate the in-memory store behind Report Metadata (2000000139) with one row per
    /// report the runner knows about. Idempotent per (provider, report id).
    /// </summary>
    private static void PopulateReportMetadataVirtualTable(object dataAccess, NCLMetaTable metaTable)
    {
        EnsureAllObjReflection(metaTable);
        EnsureReportMetadataReflection(metaTable);
        EnsureDataAccessProviderReflection(dataAccess);

        var provider = _pDataAccessDataProvider!.GetValue(dataAccess)
            ?? throw new RunnerOutOfScopeException(
                "Report Metadata (virtual table 2000000139)",
                "report-metadata-virtual-table — data access has no in-memory provider; see docs/scope.md");

        var done = _rmvPopulatedByProvider.GetValue(provider, static _ => new ConcurrentDictionary<int, byte>());

        foreach (var report in EnumerateKnownReports())
        {
            if (!done.TryAdd(report.Id, 0)) continue;
            InsertVirtualRow(provider, metaTable,
                new object[] { ReportMetadataVirtualTableId, report.Id, 0, 0 },
                field => BuildReportMetadataValue(field, report));
        }
    }

    /// <summary>
    /// Populate the in-memory store behind Report Data Items (2000000203) with one row per
    /// data item of every report the runner knows about. Idempotent per (provider, report
    /// id, data-item ordinal).
    /// </summary>
    private static void PopulateReportDataItemsVirtualTable(object dataAccess, NCLMetaTable metaTable)
    {
        EnsureAllObjReflection(metaTable);
        EnsureReportMetadataReflection(metaTable);
        EnsureDataAccessProviderReflection(dataAccess);

        var provider = _pDataAccessDataProvider!.GetValue(dataAccess)
            ?? throw new RunnerOutOfScopeException(
                "Report Data Items (virtual table 2000000203)",
                "report-data-items-virtual-table — data access has no in-memory provider; see docs/scope.md");

        var done = _rdiPopulatedByProvider.GetValue(provider, static _ => new ConcurrentDictionary<(int, int), byte>());

        foreach (var report in EnumerateKnownReports())
            foreach (var item in report.DataItems)
            {
                if (!done.TryAdd((report.Id, item.Id), 0)) continue;
                InsertVirtualRow(provider, metaTable,
                    new object[] { ReportDataItemsVirtualTableId, report.Id, item.Id, 0 },
                    field => BuildReportDataItemValue(field, report, item));
            }
    }

    /// <summary>
    /// Build and Insert one virtual row: BC's own GetSystemPopulatedVirtualRecordValues
    /// fills the timestamp / SystemId / audit slots, <paramref name="buildValue"/> answers
    /// the columns we know, and BC's own GetDefaultNavValue fills the rest.
    /// </summary>
    private static void InsertVirtualRow(
        object provider, NCLMetaTable metaTable, object[] systemIdArgs, Func<NCLMetaField, object?> buildValue)
    {
        var values = _aovSystemValues!.Invoke(metaTable, systemIdArgs);

        foreach (var field in GetAllFields(metaTable) ?? Enumerable.Empty<NCLMetaField>())
        {
            var idx = field.FieldIndex;
            if (idx < 0 || idx >= values.Length) continue;
            if (values.GetValue(idx) != null) continue;   // BC already filled this slot
            values.SetValue(buildValue(field), idx);
        }

        var readOnly = _aovCtorReadOnlyBuffer!.Invoke(new object?[] { metaTable, values });
        var mutable = _aovCtorMutableBuffer!.Invoke(new object?[] { readOnly });
        try
        {
            _aovTtdpInsert!.Invoke(provider, new object?[] { 0, mutable, _aovInsertOptionsNone, null });
        }
        catch (TargetInvocationException tie) when (
            tie.InnerException?.GetType().Name == "NavRecordAlreadyExistsException")
        {
            // Primary key already present — faithful for a virtual table keyed on it.
        }
    }

    /// <summary>
    /// One column of a Report Metadata row, matched by the metatable's own FIELD NAME so
    /// the mapping tracks whatever the System package in the resolved artifact declares
    /// rather than a hardcoded field-number table. Columns the runner cannot answer
    /// truthfully (paper source, timeout, scheduling, app id, …) get BC's own default —
    /// which is also what a real row carries for a report that declares none of them.
    /// </summary>
    private static object? BuildReportMetadataValue(NCLMetaField field, ReportRow report)
    {
        object? Text(string s) => _aovNavTextCreateTruncated!.Invoke(null, new object?[] { field.FieldDefinedLength, s ?? string.Empty });

        switch (NormalizeObjectTypeName(field.FieldName ?? string.Empty))
        {
            case "id":
                return _aovNavIntegerCreate!.Invoke(null, new object?[] { report.Id });
            case "name":
                return Text(report.Name);
            case "caption":
                return Text(report.Caption);
            case "userequestpage":
                return NavBoolean(report.UseRequestPage);
            case "processingonly":
                return NavBoolean(report.ProcessingOnly);
            case "firstdataitemtableid":
                return _aovNavIntegerCreate!.Invoke(null, new object?[] { report.FirstDataItemTableId });
            case "wordmergedataitem":
                return Text(report.WordMergeDataItem);
            default:
                return _aovGetDefaultNavValue!.Invoke(null, new object?[] { field, false });
        }
    }

    private static object? BuildReportDataItemValue(NCLMetaField field, ReportRow report, ReportDataItemRow item)
    {
        object? Text(string s) => _aovNavTextCreateTruncated!.Invoke(null, new object?[] { field.FieldDefinedLength, s ?? string.Empty });

        switch (NormalizeObjectTypeName(field.FieldName ?? string.Empty))
        {
            case "reportid":
                return _aovNavIntegerCreate!.Invoke(null, new object?[] { report.Id });
            case "dataitemid":
                return _aovNavIntegerCreate!.Invoke(null, new object?[] { item.Id });
            case "name":
                return Text(item.Name);
            case "relatedtableid":
                return _aovNavIntegerCreate!.Invoke(null, new object?[] { item.RelatedTableId });
            case "indentationlevel":
                return _aovNavIntegerCreate!.Invoke(null, new object?[] { item.Indentation });
            case "dataitemtableview":
                return Text(item.DataItemTableView);
            case "requestfilterfields":
                return Text(item.RequestFilterFields);
            // "Sorting Fields" is derived from the table view's sorting(...) clause on a
            // real tier; the runner does not parse that expression, so it gets the type
            // default rather than a guess. The full view text is on the row above it.
            default:
                return _aovGetDefaultNavValue!.Invoke(null, new object?[] { field, false });
        }
    }

    /// <summary>
    /// Every report the runner has real metadata for: source-parsed reports of the app
    /// under test and of any source-compiled dependency first, then reports declared by
    /// the SymbolReference.json of every registered precompiled dependency .app.
    /// Built once per run — the inventory does not change after compile.
    /// </summary>
    private static List<ReportRow> EnumerateKnownReports()
    {
        var generation = (_bcAppPaths.Count, _parsedReports.Count);
        if (_reportRows != null && _reportRowsBuiltFrom == generation) return _reportRows;
        lock (_reportRowsLock)
        {
            generation = (_bcAppPaths.Count, _parsedReports.Count);
            if (_reportRows != null && _reportRowsBuiltFrom == generation) return _reportRows;

            var rows = new Dictionary<int, ReportRow>();
            var tableIdCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var unresolved = new List<string>();

            int ResolveTable(string name)
            {
                if (string.IsNullOrEmpty(name)) return -1;
                if (tableIdCache.TryGetValue(name, out var cached)) return cached;
                var id = ResolveTableIdByName(name);
                tableIdCache[name] = id;
                return id;
            }

            // 1. Reports the runner source-compiled.
            foreach (var parsed in _parsedReports.Values)
            {
                var items = new List<ReportDataItemRow>();
                bool ok = true;
                foreach (var di in parsed.DataItems)
                {
                    int tableId = ResolveTable(di.RelatedTable);
                    if (tableId <= 0)
                    {
                        unresolved.Add($"report {parsed.Id} dataitem {di.Name} -> table '{di.RelatedTable}'");
                        ok = false;
                        break;
                    }
                    items.Add(new ReportDataItemRow(di.Ordinal, di.Name, tableId, di.Indentation,
                        di.DataItemTableView ?? string.Empty, di.RequestFilterFields ?? string.Empty));
                }
                if (!ok) continue;

                rows[parsed.Id] = new ReportRow(
                    parsed.Id, parsed.Name, parsed.Caption ?? parsed.Name,
                    parsed.ProcessingOnly, parsed.UseRequestPage,
                    WordMergeDataItem: string.Empty,
                    FirstDataItemTableId: FirstRootTableId(items),
                    DataItems: items);
            }

            // 2. Reports declared by precompiled dependency .app packages.
            foreach (var symbol in EnumerateBcAppReportSymbols())
            {
                if (rows.ContainsKey(symbol.Id)) continue;   // source-compiled wins
                var items = new List<ReportDataItemRow>();
                bool ok = true;
                int ordinal = 0;
                foreach (var di in symbol.DataItems)
                {
                    ordinal++;
                    int tableId = ResolveTable(di.RelatedTable);
                    if (tableId <= 0)
                    {
                        unresolved.Add($"report {symbol.Id} dataitem {di.Name} -> table '{di.RelatedTable}'");
                        ok = false;
                        break;
                    }
                    // The symbol file's own compiler-assigned data-item id when it has one,
                    // else declaration order. Never a fabricated hash.
                    items.Add(new ReportDataItemRow(di.Id != 0 ? di.Id : ordinal, di.Name, tableId, di.Indentation,
                        di.DataItemTableView ?? string.Empty, di.RequestFilterFields ?? string.Empty));
                }
                if (!ok) continue;

                rows[symbol.Id] = new ReportRow(
                    symbol.Id, symbol.Name, symbol.Caption ?? symbol.Name,
                    symbol.ProcessingOnly, symbol.UseRequestPage,
                    symbol.WordMergeDataItem ?? string.Empty,
                    FirstRootTableId(items), items);
            }

            if (unresolved.Count > 0 && Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_REPORT_METADATA") == "1")
                Console.Out.WriteLine(
                    $"[report-metadata] omitted: {string.Join("; ", unresolved.Take(40))}");
            if (unresolved.Count > 0)
                Console.Error.WriteLine(
                    $"[RecordPatches] Report Metadata: omitted {unresolved.Count} report(s) whose data-item table "
                    + $"could not be resolved — Get() answers false for them rather than claiming they have no "
                    + $"dataset: {string.Join("; ", unresolved.Take(10))}"
                    + (unresolved.Count > 10 ? $" (+{unresolved.Count - 10} more)" : string.Empty));

            _reportRows = rows.Values.ToList();
            _reportRowsBuiltFrom = generation;
            // Diagnostic channel is stdout on purpose: the test-execution child's stderr is
            // not captured, so a Console.Error trace here would be invisible exactly when
            // it is needed. Env-gated so a normal run stays quiet.
            var trace = Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_REPORT_METADATA");
            if (!string.IsNullOrEmpty(trace))
            {
                Console.Out.WriteLine(
                    $"[report-metadata] {_reportRows.Count} report(s) known "
                    + $"({_parsedReports.Count} source-parsed, {_bcAppPaths.Count} dependency .app(s)); "
                    + $"{_reportRows.Count(r => !_parsedReports.ContainsKey(r.Id))} of them from dependency symbols");
                // Set the variable to a report id to dump that one report's resolved row.
                if (int.TryParse(trace, out var probeId) && probeId > 1)
                    Console.Out.WriteLine(rows.TryGetValue(probeId, out var probe)
                        ? $"[report-metadata] {probeId}: name='{probe.Name}' caption='{probe.Caption}' "
                          + $"processingOnly={probe.ProcessingOnly} firstDataItemTable={probe.FirstDataItemTableId} "
                          + $"dataItems=[{string.Join(", ", probe.DataItems.Select(d => $"{d.Name}@{d.Indentation}->{d.RelatedTableId}"))}]"
                        : $"[report-metadata] {probeId}: NOT KNOWN");
            }
            return _reportRows;
        }
    }

    /// <summary>
    /// Ids of every report the runner knows exists — source-parsed ones plus every report
    /// declared by a registered dependency .app. Deliberately does NOT resolve data-item
    /// tables (unlike <see cref="EnumerateKnownReports"/>): callers that only need
    /// existence, such as the NCLMetaReport skeleton populator, must not pay for — or be
    /// perturbed by — faulting hundreds of BC tables into _parsedTables.
    /// </summary>
    internal static int[] KnownReportIds() => KnownReportIdSet().ToArray();

    private static HashSet<int>? _knownReportIds;
    // Assembly count is part of the generation: a Tier-1 precompiled dependency's reports
    // only become knowable once its DLL is loaded, which happens AFTER this set is first
    // asked for. Without it the empty answer would be memoized for the whole run.
    private static (int Apps, int Parsed, int Assemblies) _knownReportIdsBuiltFrom = (-1, -1, -1);

    /// <summary>Memoized backing set, rebuilt on the same generation key as the row cache.</summary>
    internal static HashSet<int> KnownReportIdSet()
    {
        var generation = (_bcAppPaths.Count, _parsedReports.Count,
            AppDomain.CurrentDomain.GetAssemblies().Length);
        if (_knownReportIds != null && _knownReportIdsBuiltFrom == generation) return _knownReportIds;

        var ids = new HashSet<int>(_parsedReports.Keys);
        foreach (var symbol in EnumerateBcAppReportSymbols())
            ids.Add(symbol.Id);
        foreach (var id in CompiledReportIds())
            ids.Add(id);
        _knownReportIds = ids;
        _knownReportIdsBuiltFrom = generation;
        return ids;
    }

    /// <summary>
    /// The table of the first ROOT (indentation 0) data item — what BC reports as
    /// FirstDataItemTableID. 0 when the report declares no data item at all, which is the
    /// value callers read as "processing-only / nothing to lay out".
    /// </summary>
    private static int FirstRootTableId(List<ReportDataItemRow> items)
    {
        foreach (var i in items)
            if (i.Indentation == 0) return i.RelatedTableId;
        return 0;
    }

    /// <summary>
    /// Report ids that exist as a compiled <c>Report{id}</c> type in a loaded assembly.
    ///
    /// The other two sources both need something the runner does not always have: AL source
    /// it compiled itself, or a <c>SymbolReference.json</c> inside a dependency's .app. A
    /// TIER-1 PRECOMPILED dependency has neither — DependencyLoader loads its DLL directly
    /// and never extracts or compiles AL — so its reports were unknown, and
    /// <c>Report.SaveAs</c> on one failed with BC's "metadata object Report N was not found /
    /// the application might be incompatible with the current compiler" rather than running.
    /// The compiled type is proof the report exists, which is exactly what this set answers.
    /// </summary>
    private static IEnumerable<int> CompiledReportIds()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch { continue; } // dynamic/reflection-only assemblies — nothing to learn here
            foreach (var t in types)
            {
                if (!t.Name.StartsWith("Report", StringComparison.Ordinal)) continue;
                if (!int.TryParse(t.Name.AsSpan(6), out var id) || id <= 0) continue;
                yield return id;
            }
        }
    }

    private static IEnumerable<BcAppSymbolCache.ReportSymbol> EnumerateBcAppReportSymbols()
    {
        foreach (var appPath in _bcAppPaths.ToArray())
        {
            List<BcAppSymbolCache.ReportSymbol> reports;
            try
            {
                reports = BcAppSymbolCache.Get(appPath).Reports;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[RecordPatches] Report Metadata: SymbolReference read failed for {Path.GetFileName(appPath)}: {ex.Message}");
                continue;
            }
            foreach (var r in reports)
                yield return r;
        }
    }

    private static MethodInfo? _rmvNavBooleanCreate;

    private static object? NavBoolean(bool value)
        => _rmvNavBooleanCreate!.Invoke(null, new object?[] { value });

    /// <summary>
    /// NavBoolean.Create(bool) — the only helper these two tables need beyond the set the
    /// AllObj provider already resolves. Bound off the metatable's own assembly with a hard
    /// throw when absent, never a silently skipped column.
    /// </summary>
    private static void EnsureReportMetadataReflection(NCLMetaTable metaTable)
    {
        if (_rmvNavBooleanCreate != null) return;

        var tNavBoolean = ResolveType("Microsoft.Dynamics.Nav.Runtime.NavBoolean", "Microsoft.Dynamics.Nav.Types.NavBoolean")
            ?? throw new InvalidOperationException("NavBoolean type not found — BC metadata shape changed");

        _rmvNavBooleanCreate = tNavBoolean.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "Create"
                && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType == typeof(bool))
            ?? throw new InvalidOperationException("NavBoolean.Create(bool) not found — BC metadata shape changed");
    }
}
