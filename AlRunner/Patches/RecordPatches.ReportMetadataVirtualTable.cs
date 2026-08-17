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
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
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
    /// Per-assembly memo for <see cref="CompiledReportIds"/> — see that method's remarks
    /// for why this exists. Keyed by reference identity (an Assembly instance never changes
    /// once loaded), valued by the Report{id} ids that assembly's <c>GetTypes()</c> yielded.
    /// A <c>ConcurrentDictionary</c> because <see cref="KnownReportIdSet"/> can be reached
    /// from more than one call site (PopulateNclMetadataCache and
    /// NclMetaFormReportBuilder) without a shared lock ordering guarantee.
    /// </summary>
    private static readonly ConcurrentDictionary<Assembly, int[]> _compiledReportIdsByAssembly = new();

    /// <summary>
    /// Test-only probe: true once <paramref name="asm"/> has an entry in
    /// <see cref="_compiledReportIdsByAssembly"/>, regardless of whether that entry came from
    /// <see cref="SeedCompiledReportIdsFromPEBytes"/> (no <c>GetTypes()</c> call) or from
    /// <see cref="CompiledReportIds"/>'s own lazy fallback scan. #1852's proving test uses
    /// this to assert the PE-byte seed path populates the cache without ever reaching the
    /// slow path for that assembly.
    /// </summary>
    internal static bool IsCompiledReportIdsSeeded(Assembly asm) => _compiledReportIdsByAssembly.ContainsKey(asm);

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
    ///
    /// PER-ASSEMBLY MEMOIZED (#1852): <see cref="KnownReportIdSet"/>'s own generation key
    /// includes the loaded-assembly count, so every newly loaded assembly (test-emitted
    /// output, a lazily-loaded BC dependency DLL, ...) busts that outer cache and this method
    /// runs again. Before this memo, EVERY call re-scanned EVERY loaded assembly from
    /// scratch via <c>Assembly.GetTypes()</c> — including the huge precompiled
    /// BaseApplication/SystemApplication DLLs already scanned by a prior call. Measured on
    /// this repo's own boot sequence: the first scan over ~189 assemblies cost ~17.7s, and an
    /// immediate repeat over the SAME assembly set (nothing newly loaded) cost only ~207ms —
    /// the type-loading work itself is the expensive part, and it was being redone in full on
    /// every cache-busting call instead of once per assembly for the process's lifetime.
    /// Caching per-assembly turns that into O(assemblies) total reflection work across the
    /// whole process, not O(calls × assemblies).
    /// </summary>
    private static IEnumerable<int> CompiledReportIds()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (_compiledReportIdsByAssembly.TryGetValue(asm, out var cached))
            {
                foreach (var id in cached) yield return id;
                continue;
            }

            // Metadata-first: the question this method asks — "does a type called
            // Report{id} exist in this assembly" — is answerable from the TypeDef table's Name
            // column alone, which is EXACTLY what SeedCompiledReportIdsFromPEBytes already does
            // for DependencyLoader-loaded assemblies (and what #1852's equivalence test proved
            // agrees with the GetTypes() path on the identical TypeDef set). Doing the same for
            // every OTHER loaded assembly removes the last Assembly.GetTypes() call on this
            // path: measured at 2.0s of a 16.4s warm invocation, all of it type-loading the
            // Base App chunks that nothing else in the run ever needed loaded.
            //
            // It also has no partial-load failure mode at all — there is no
            // ReflectionTypeLoadException to half-answer — so the result is always complete
            // and always safe to cache.
            var index = AlRunner.Infrastructure.AssemblyTypeIndex.For(asm);
            if (index.IsMetadataBacked)
            {
                var mdIds = ExtractReportIdsFromNames(index.TypeNamesWithPrefix("Report"));
                _compiledReportIdsByAssembly[asm] = mdIds;
                foreach (var id in mdIds) yield return id;
                continue;
            }

            // Dynamic assemblies have no metadata to read; they keep the original reflection
            // path (and its deliberate no-cache-on-exception semantics) unchanged.
            Type[]? types;
            int[]? partialIdsOnException = null;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException rtle)
            {
                // A partial load — typically an assembly scanned before all of its own
                // dependencies are loaded yet, which #1852's own investigation established
                // DOES happen mid-spawn (assemblies keep arriving through a spawn's life).
                // ex.Types carries whatever DID load; use it for THIS yield, but do not cache
                // it — see the "no cache write" note below for why a partial answer must
                // never become the permanent one. (Can't yield from inside a catch block, so
                // stash it and yield once we're back in the loop body below.)
                partialIdsOnException = ExtractReportIds(rtle.Types);
                types = null;
            }
            catch
            {
                // Anything else that makes GetTypes() unusable for this assembly (dynamic /
                // reflection-only assemblies, etc.) — try again on a later call rather than
                // caching a wrong answer now that the assembly might be more loadable later.
                types = null;
            }

            if (types is null)
            {
                if (partialIdsOnException is not null)
                    foreach (var id in partialIdsOnException) yield return id;
                continue;
            }

            // NO CACHE WRITE ON EXCEPTION (#1852 review): the original code's `catch { continue; }`
            // never cached a failed scan, so a LATER call — once the assembly's dependencies
            // are actually loaded — retries it from scratch and can find its ids. Caching an
            // empty/partial result on exception would instead memoize an incomplete answer for
            // the rest of the process, and a real report id going missing from KnownReportIdSet
            // surfaces as BC's "metadata object Report N was not found" — the exact failure
            // this whole set exists to prevent. Only a SUCCESSFUL, complete GetTypes() call
            // (or the PE-byte path below, which has no such partial-load failure mode at all)
            // gets a permanent cache entry.
            var idsArray = ExtractReportIds(types);
            _compiledReportIdsByAssembly[asm] = idsArray;
            foreach (var id in idsArray) yield return id;
        }
    }

    /// <summary>
    /// The one gate both discovery paths apply to a TypeDef name: a <c>Report{id}</c> name
    /// (numeric suffix, no leading-zero requirement beyond <c>int.TryParse</c>, id must be
    /// positive) yields its id; anything else — including a name that merely starts with
    /// "Report" but has a non-numeric suffix — yields nothing. Actually SHARED code (not just
    /// parallel implementations of the same rule), called from both
    /// <see cref="ExtractReportIds"/> (the <c>GetTypes()</c> path) and
    /// <see cref="ScanReportIdsFromPeBytes"/> (the <c>MetadataReader</c> path, which only ever
    /// has a name string, never a <c>Type</c>), so the two discovery mechanisms can only ever
    /// agree or disagree on WHICH ids exist, never on what counts as one.
    /// </summary>
    private static bool TryParseReportId(ReadOnlySpan<char> typeName, out int id)
    {
        id = 0;
        if (!typeName.StartsWith("Report", StringComparison.Ordinal)) return false;
        return int.TryParse(typeName[6..], out id) && id > 0;
    }

    /// <summary>
    /// Shared by both discovery paths (<see cref="CompiledReportIds"/>'s <c>GetTypes()</c>
    /// scan and <see cref="ScanReportIdsFromPeBytes"/>'s <c>MetadataReader</c> scan) via
    /// <see cref="TryParseReportId"/> — see that method's remarks for the shared gate.
    /// </summary>
    /// <summary>
    /// Name-only sibling of <see cref="ExtractReportIds"/>, applying the identical
    /// <see cref="TryParseReportId"/> gate to raw TypeDef names — used by the metadata path in
    /// <see cref="CompiledReportIds"/> and equivalent by construction to
    /// <see cref="ScanReportIdsFromPeBytes"/>.
    /// </summary>
    private static int[] ExtractReportIdsFromNames(IEnumerable<string> typeNames)
    {
        List<int>? ids = null;
        foreach (var name in typeNames)
        {
            if (!TryParseReportId(name, out var id)) continue;
            (ids ??= new List<int>()).Add(id);
        }
        return ids?.ToArray() ?? Array.Empty<int>();
    }

    private static int[] ExtractReportIds(IEnumerable<Type?> types)
    {
        List<int>? ids = null;
        foreach (var t in types)
        {
            if (t is null) continue; // ReflectionTypeLoadException.Types can contain nulls for the types that failed to load
            if (!TryParseReportId(t.Name, out var id)) continue;
            (ids ??= new List<int>()).Add(id);
        }
        return ids?.ToArray() ?? Array.Empty<int>();
    }

    /// <summary>
    /// Test-only: exercises the exact same <c>GetTypes()</c> + name-gate logic
    /// <see cref="CompiledReportIds"/> uses for one assembly, without touching the cache or
    /// any other loaded assembly. #1852's equivalence test reads this directly instead of
    /// diffing <see cref="KnownReportIdSet"/>'s process-wide union, which cannot isolate one
    /// assembly's contribution once two assemblies share identical Report{id} names (a plain
    /// <c>HashSet&lt;int&gt;</c> of ids carries no assembly provenance).
    /// </summary>
    internal static int[] ReadReportIdsViaGetTypesForTest(Assembly asm) => ExtractReportIds(asm.GetTypes());

    /// <summary>
    /// Core PE-byte scan behind <see cref="SeedCompiledReportIdsFromPEBytes"/> — reads
    /// <c>Report{id}</c> TypeDef names directly from raw PE bytes via
    /// <c>System.Reflection.Metadata</c>, no <c>Assembly.Load</c> or <c>GetTypes()</c>
    /// involved. Returns <see langword="null"/> for an unreadable/malformed image (no
    /// metadata, corrupt stream, ...) so the caller can distinguish "found nothing" from
    /// "couldn't read this at all".
    /// </summary>
    private static int[]? ScanReportIdsFromPeBytes(byte[] peBytes)
    {
        try
        {
            using var peReader = new System.Reflection.PortableExecutable.PEReader(
                new MemoryStream(peBytes, writable: false));
            if (!peReader.HasMetadata) return null;
            var mr = peReader.GetMetadataReader();
            List<int>? ids = null;
            foreach (var th in mr.TypeDefinitions)
            {
                var name = mr.GetString(mr.GetTypeDefinition(th).Name);
                if (!TryParseReportId(name, out var id)) continue;
                (ids ??= new List<int>()).Add(id);
            }
            return ids?.ToArray() ?? Array.Empty<int>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Pre-warms <see cref="_compiledReportIdsByAssembly"/> for one just-loaded assembly
    /// directly from its raw PE bytes — reading only the TypeDef table's Name strings, never
    /// materializing a <c>RuntimeType</c> (no <c>Assembly.GetTypes()</c> call at all).
    ///
    /// WHY: measured on this repo's own boot, <c>Assembly.GetTypes()</c> over the R2R DLL
    /// chunks DependencyLoader loads for BaseApplication/SystemApplication (Tier 1/2, tens of
    /// thousands of types each) cost 0.7s–4.3s PER ASSEMBLY, ~17.7s total across one bundle's
    /// six chunks — the dominant single cost in a cold spawn's engine boot. Those assemblies
    /// are loaded via <c>Assembly.Load(byte[])</c>, so <c>asm.Location</c> is empty and
    /// <see cref="CompiledReportIds"/> could not re-derive a file path to read metadata from
    /// more cheaply after the fact. DependencyLoader already holds the raw bytes at load
    /// time, so seeding here means <see cref="CompiledReportIds"/> never has to call
    /// <c>GetTypes()</c> on these assemblies at all — same TypeDef table, same ids, just read
    /// through the metadata-only reader instead of the CLR's full type-loading machinery.
    ///
    /// A malformed/unreadable image is not an error here: it just skips the pre-warm, and
    /// <see cref="CompiledReportIds"/> falls back to its normal (slower but correct)
    /// <c>GetTypes()</c> path for that one assembly on first ask.
    /// </summary>
    internal static void SeedCompiledReportIdsFromPEBytes(Assembly asm, byte[] peBytes)
    {
        if (_compiledReportIdsByAssembly.ContainsKey(asm)) return;
        var ids = ScanReportIdsFromPeBytes(peBytes);
        if (ids is null) return; // unreadable — leave unseeded, CompiledReportIds() falls back to GetTypes() lazily
        _compiledReportIdsByAssembly[asm] = ids;
    }

    /// <summary>
    /// Path-based sibling of <see cref="SeedCompiledReportIdsFromPEBytes"/> (issue #perf-B):
    /// DependencyLoader's R2R chunk path now loads each chunk via
    /// <c>AssemblyLoadContext.LoadFromAssemblyPath</c> (memory-mapped, on-disk cache) rather
    /// than <c>Assembly.Load(byte[])</c>, so it no longer holds the chunk's bytes in memory
    /// after the load — reading them back out just to pre-warm this cache would reintroduce
    /// the exact per-invocation cost #1852 removed. <see cref="System.Reflection.PortableExecutable.PEReader"/>
    /// accepts a <see cref="Stream"/> directly and reads metadata lazily off it, so this scans
    /// the SAME TypeDef table straight from the file on disk, never materializing the whole
    /// DLL as a byte[].
    /// </summary>
    internal static void SeedCompiledReportIdsFromPeFile(Assembly asm, string dllPath)
    {
        if (_compiledReportIdsByAssembly.ContainsKey(asm)) return;
        var ids = ScanReportIdsFromPeFile(dllPath);
        if (ids is null) return; // unreadable — leave unseeded, CompiledReportIds() falls back to GetTypes() lazily
        _compiledReportIdsByAssembly[asm] = ids;
    }

    private static int[]? ScanReportIdsFromPeFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var peReader = new System.Reflection.PortableExecutable.PEReader(fs);
            if (!peReader.HasMetadata) return null;
            var mr = peReader.GetMetadataReader();
            List<int>? ids = null;
            foreach (var th in mr.TypeDefinitions)
            {
                var name = mr.GetString(mr.GetTypeDefinition(th).Name);
                if (!TryParseReportId(name, out var id)) continue;
                (ids ??= new List<int>()).Add(id);
            }
            return ids?.ToArray() ?? Array.Empty<int>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Test-only: exercises the exact same PE-byte/<c>MetadataReader</c> scan
    /// <see cref="SeedCompiledReportIdsFromPEBytes"/> uses, without touching the cache.
    /// See <see cref="ReadReportIdsViaGetTypesForTest"/> for the counterpart on the other path.
    /// </summary>
    internal static int[] ReadReportIdsFromPeBytesForTest(byte[] peBytes) => ScanReportIdsFromPeBytes(peBytes) ?? Array.Empty<int>();

    /// <summary>Test-only: the file-based counterpart of <see cref="ReadReportIdsFromPeBytesForTest"/>,
    /// exercising <see cref="ScanReportIdsFromPeFile"/> directly.</summary>
    internal static int[] ReadReportIdsFromPeFileForTest(string path) => ScanReportIdsFromPeFile(path) ?? Array.Empty<int>();

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
