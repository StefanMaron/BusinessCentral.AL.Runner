// RecordPatches.BcAppFallback — populate _parsedTables on demand from BC .app
// dependency packages when AL test source doesn't define the requested table.
//
// Why: tests under tests/spike-a-baseapp (and any integration test that touches
// a Base App / System App table such as Currency = table 4) fail with
//   "no NCLMetaTable for table N (AL source not parsed)"
// because BuildNCLMetaTable only consults _parsedTables, populated from the
// test suite's own src/ directory. The compiled Record{N} : NavRecord type IS
// loaded (Tier 2 R2R), but it doesn't carry table-shape attributes — field
// metadata in BC compiled apps lives as AL source inside the .app NAVX zip.
//
// Per .claude/rules/precompiled-dll-respect.md the fix is upstream from the
// AL business logic: when a table id is missing from _parsedTables, walk the
// list of dependency .app files (registered by Program.cs after dep load),
// extract the matching `*.Table.al` source via AppLoader.ExtractAl, run it
// through the existing TryParseTableFile, and the rest of BuildNCLMetaTable
// proceeds unchanged.
//
// Performance: index built lazily on first miss by scanning each .app's
// AL sources for `table <id>` declarations. The result (tableId → appPath)
// is cached so subsequent misses are O(1). Negative misses are also cached
// so a non-existent table doesn't re-scan every .app on every Init().

using System.Reflection;
using System.Text.RegularExpressions;

namespace AlRunnerV2.Patches;

public static partial class RecordPatches
{
    // .app file paths registered by Program.cs after DependencyLoader.LoadAll.
    private static readonly List<string> _bcAppPaths = new();

    // Temp .app file extracted from Microsoft.BusinessCentral.SystemApp.dll's embedded
    // SystemPackage; persists for the lifetime of the runner process so the index can
    // re-read its source on demand.
    private static string? _systemAppTempPath;

    // Lazy index: tableId → (appPath, alSource). Built on first miss.
    private static Dictionary<int, (string AppPath, string Source)>? _bcTableIndex;
    private static readonly object _bcTableIndexLock = new();

    // Negative cache: tableIds we've already tried and not found.
    private static readonly HashSet<int> _bcMissCache = new();

    /// <summary>
    /// Register a BC dependency .app path so its AL table sources can be used
    /// as a fallback when a test's own src/ doesn't define a referenced table.
    /// Called from Program.cs after DependencyLoader.LoadAll.
    /// </summary>
    public static void AddBcAppPath(string appPath)
    {
        if (string.IsNullOrEmpty(appPath) || !File.Exists(appPath)) return;
        lock (_bcTableIndexLock)
        {
            if (!_bcAppPaths.Contains(appPath, StringComparer.OrdinalIgnoreCase))
            {
                _bcAppPaths.Add(appPath);
                AlRunnerV2.AlEnumMetadataRegistry.RegisterFromAppPath(appPath);
                // Invalidate the index so newly-added .app gets picked up on next miss.
                _bcTableIndex = null;
            }
        }
    }

    /// <summary>
    /// On _parsedTables miss for tableId, scan registered BC .app dependencies,
    /// find the matching `table <id>` declaration, and feed it through
    /// TryParseTableFile so _parsedTables gets populated. Returns true iff a
    /// matching table source was found and parsed.
    /// </summary>
    private static bool TryPopulateParsedTableFromBcApps(int tableId)
    {
        lock (_bcTableIndexLock)
        {
            if (_bcMissCache.Contains(tableId)) return false;
            EnsureBcTableIndex();
            if (_bcTableIndex == null || !_bcTableIndex.TryGetValue(tableId, out var entry))
            {
                _bcMissCache.Add(tableId);
                return false;
            }
            // Parse the source slice that contains this table id.
            TryParseTableFile(entry.Source);
            if (_parsedTables.ContainsKey(tableId))
            {
                Console.Error.WriteLine($"[RecordPatches] BcAppFallback: parsed table {tableId} from {Path.GetFileName(entry.AppPath)}");
                return true;
            }
            // Source had a `table N` regex match but TryParseTableFile didn't materialise
            // it — likely a non-table object reusing the keyword. Treat as miss.
            _bcMissCache.Add(tableId);
            return false;
        }
    }

    private static readonly Regex _rxAnyTableId = new(
        @"\btable\s+(\d+)\s+(?:""[^""]+""|[A-Za-z_]\w*)[^{]*?\{",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Microsoft.BusinessCentral.SystemApp.dll embeds the AL source for NCL-internal
    /// system tables (RecordLink=2000000068, Field=2000000041, Object=2000000038, …)
    /// inside a SystemPackage NAVX stream. Extract it to a temp .app, register the
    /// path with BcAppFallback, and eagerly parse every table the package contains so
    /// PopulateNclMetadataCache writes them to NCLMetadata's cache dict.
    ///
    /// Why eagerly: BC's own NCL code (e.g. `RecordLink.AddLinkAsync` →
    /// `new NavRecord(record, 2000000068)`) calls `NCLMetadata.GetMetaTableById`
    /// directly — bypassing our NavRecordHandle_CreateTarget hook — so lazy
    /// BcAppFallback never fires; the cache dict must be primed up front.
    /// </summary>
    internal static void RegisterSystemAppPackage()
    {
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.BusinessCentral.SystemApp");
            if (asm == null)
            {
                try { asm = Assembly.Load("Microsoft.BusinessCentral.SystemApp"); }
                catch { /* fall through */ }
            }
            if (asm == null)
            {
                Console.Error.WriteLine("[RecordPatches] BcAppFallback: SystemApp assembly not loadable; system tables (RecordLink etc.) will fail");
                return;
            }

            var tSystemPackage = asm.GetTypes().FirstOrDefault(t => t.Name == "SystemPackage");
            var mGetStream = tSystemPackage?.GetMethod("GetPackageStream",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            if (mGetStream == null)
            {
                Console.Error.WriteLine("[RecordPatches] BcAppFallback: SystemPackage.GetPackageStream not found in SystemApp DLL");
                return;
            }

            using var stream = (Stream)mGetStream.Invoke(null, null)!;
            var tempPath = Path.Combine(Path.GetTempPath(), $"al-runner-systemapp-{Guid.NewGuid():N}.app");
            using (var fs = File.Create(tempPath))
                stream.CopyTo(fs);

            _systemAppTempPath = tempPath;
            AddBcAppPath(tempPath);
            Console.Error.WriteLine($"[RecordPatches] BcAppFallback: registered SystemPackage → {Path.GetFileName(tempPath)} ({new FileInfo(tempPath).Length:N0} bytes)");

            EagerParseAllBcAppTables();
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            Console.Error.WriteLine($"[RecordPatches] BcAppFallback: SystemApp registration failed: {inner.GetType().Name}: {inner.Message}");
        }
    }

    /// <summary>
    /// Walk every (tableId, source) the BC .app index discovered and feed the source
    /// through TryParseTableFile so its tables land in _parsedTables. Idempotent —
    /// already-parsed table ids are skipped, and TryParseTableFile is safe to call
    /// repeatedly on the same text.
    /// </summary>
    internal static void EagerParseAllBcAppTables()
    {
        lock (_bcTableIndexLock)
        {
            EnsureBcTableIndex();
            if (_bcTableIndex == null) return;
            int parsedNow = 0;
            var alreadySeenSources = new HashSet<string>(ReferenceEqualityComparer.Instance);
            foreach (var (id, entry) in _bcTableIndex)
            {
                if (_parsedTables.ContainsKey(id)) continue;
                // Same Source string may map from many ids when one .al holds multiple
                // tables — skip duplicates so we don't re-parse identical text.
                if (!alreadySeenSources.Add(entry.Source)) continue;
                TryParseTableFile(entry.Source);
                if (_parsedTables.ContainsKey(id)) parsedNow++;
            }
            if (parsedNow > 0)
                Console.Error.WriteLine($"[RecordPatches] BcAppFallback: eager-parsed {parsedNow} BC .app table(s) into _parsedTables");
        }
    }

    private static void EnsureBcTableIndex()
    {
        if (_bcTableIndex != null) return;
        var idx = new Dictionary<int, (string, string)>();
        foreach (var appPath in _bcAppPaths)
        {
            IReadOnlyList<(string Name, string Source)> sources;
            try { sources = AlRunnerV2.AppLoader.ExtractAl(appPath); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RecordPatches] BcAppFallback: ExtractAl failed for {Path.GetFileName(appPath)}: {ex.Message}");
                continue;
            }
            foreach (var (name, source) in sources)
            {
                // Cheap pre-filter — skip files that don't contain the keyword `table`.
                if (source.IndexOf("table", StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (Match m in _rxAnyTableId.Matches(source))
                {
                    if (!int.TryParse(m.Groups[1].Value, out int id)) continue;
                    // First definition wins — Base App should always trump System App
                    // ordering by virtue of being scanned in dep-resolution order.
                    if (!idx.ContainsKey(id))
                        idx[id] = (appPath, source);
                }
            }
        }
        _bcTableIndex = idx;
        Console.Error.WriteLine($"[RecordPatches] BcAppFallback: indexed {idx.Count} table id(s) across {_bcAppPaths.Count} BC .app file(s)");
    }
}
