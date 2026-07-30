// RecordPatches.BcAppFallback — populate _parsedTables on demand from BC .app
// dependency packages when AL test source doesn't define the requested table.
//
// Why: tests under tests/spike-a-baseapp (and any integration test that touches
// a Base App / System App table such as Currency = table 4) fail with
//   "no NCLMetaTable for table N (AL source not parsed)"
// because BuildNCLMetaTable only consults _parsedTables, populated from the
// test suite's own src/ directory. The compiled Record{N} : NavRecord type IS
// loaded (Tier 2 R2R), but it doesn't carry table-shape attributes — field
// metadata in BC compiled apps lives in SymbolReference.json inside the .app
// NAVX zip (with AL source as a fallback for packages without symbols).
//
// Per .claude/rules/precompiled-dll-respect.md the fix is upstream from the
// AL business logic: when a table id is missing from _parsedTables, walk the
// list of dependency .app files (registered by Program.cs after dep load),
// read the matching table metadata from SymbolReference.json. If a package has
// no symbols, fall back to extracting the matching `*.Table.al` source via
// AppLoader.ExtractAl and feeding it through the existing parser.
//
// Performance: symbol index built lazily on first miss by reading each .app's
// SymbolReference.json (recursive namespaces). AL source extraction is only
// used as a fallback. Negative misses are cached so a non-existent table
// doesn't re-scan every .app on every Init().

using System.Reflection;
using System.Text.RegularExpressions;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    // .app file paths registered by Program.cs after DependencyLoader.LoadAll.
    private static readonly List<string> _bcAppPaths = new();

    // Temp .app file extracted from Microsoft.BusinessCentral.SystemApp.dll's embedded
    // SystemPackage; persists for the lifetime of the runner process so the index can
    // re-read its source on demand.
    private static string? _systemAppTempPath;

    // Lazy fallback index: tableId → (appPath, alSource). Built only when symbols miss.
    private static Dictionary<int, (string AppPath, string Source)>? _bcTableIndex;
    private static Dictionary<int, (string AppPath, ParsedTable Table)>? _bcSymbolTableIndex;
    // Query symbol index: queryId → QuerySymbol, built from registered .app SymbolReference.json.
    private static Dictionary<int, BcAppSymbolCache.QuerySymbol>? _bcSymbolQueryIndex;
    // Raw SymbolReference.json files registered as query-symbol-only sources (the bundle's
    // own freshly-compiled query metadata, written by BcCompiler.Emit for source-only
    // bundles that ship no prebuilt .app). Kept separate from _bcAppPaths because these
    // are loose .json files, not .app zips.
    private static readonly List<string> _bcQuerySymbolJsonPaths = new();
    // Extension index built flag. Data lands directly in _parsedExtensionFields/_extensionIdsByBaseTable.
    private static bool _bcSymbolExtensionIndexBuilt;
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
                foreach (var enumSymbol in BcAppSymbolCache.Get(appPath).Enums)
                    AlRunner.AlEnumMetadataRegistry.Register(
                        enumSymbol.Id,
                        enumSymbol.Name,
                        enumSymbol.Options.ToArray(),
                        enumSymbol.Indexes.ToArray(),
                        enumSymbol.Implementations.Select(i => i.ToArray()).ToArray());
                // Invalidate the indexes so newly-added .app gets picked up on next miss.
                _bcTableIndex = null;
                _bcSymbolTableIndex = null;
                _bcSymbolQueryIndex = null;
                _bcSymbolExtensionIndexBuilt = false;
            }
        }
    }

    /// <summary>
    /// Register any prebuilt `.app` files sitting in the bundle root (alongside the AL
    /// source) that carry a SymbolReference.json, so the runner can read the bundle's OWN
    /// query/table symbol metadata (e.g. corpus query 60022's BC-compiler-assigned column
    /// ids, which the generic NCLMetaQuery builder needs verbatim). Source-only bundles
    /// with no prebuilt .app simply have no query symbols available — queries then fall
    /// back to the null-metaquery behaviour, not a fabricated definition. Recurses one
    /// level so a bundle laid out as <root>/MainApps/* still finds its top-level .app.
    /// </summary>
    public static void RegisterBundleSymbolApps(string bundleRoot)
    {
        try
        {
            if (string.IsNullOrEmpty(bundleRoot) || !Directory.Exists(bundleRoot)) return;
            foreach (var app in Directory.EnumerateFiles(bundleRoot, "*.app", SearchOption.TopDirectoryOnly))
            {
                try { if (AlRunner.AppLoader.HasSymbolReference(app)) AddBcAppPath(app); }
                catch { /* unreadable .app — skip */ }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RecordPatches] BcAppFallback: RegisterBundleSymbolApps({bundleRoot}) failed: {ex.Message}");
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

            // Platform tables first: no .app can supply them (they have neither symbols nor
            // AL source), so scanning for them only ever produces a miss. See
            // RecordPatches.PlatformMediaTables.
            if (BuiltInPlatformTable(tableId) is { } builtIn)
            {
                _parsedTables[tableId] = builtIn;
                return true;
            }

            EnsureBcSymbolTableIndex();
            if (_bcSymbolTableIndex != null && _bcSymbolTableIndex.TryGetValue(tableId, out var symbolEntry))
            {
                _parsedTables[tableId] = symbolEntry.Table;
                Console.Error.WriteLine($"[RecordPatches] BcAppFallback: parsed table {tableId} from symbols {Path.GetFileName(symbolEntry.AppPath)}");
                return true;
            }

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

    /// <summary>
    /// On _parsedTables miss for a table referenced by NAME (e.g. a FlowField
    /// CalcFormula's source table), resolve the table id from the BC .app symbol
    /// index by name and materialise it via TryPopulateParsedTableFromBcApps.
    /// Returns the parsed ParsedTable or null. Used by BuildMetaCalcFormula so a
    /// Base App FlowField (e.g. Purchase Line "Matched Order Lines" → count of
    /// "Matched Order Line") gets a real formula instead of falling back to the
    /// null EmptyFormula (which later NREs/throws on EmptyFormula.SourceField).
    /// </summary>
    internal static ParsedTable? TryPopulateParsedTableByName(string tableName)
    {
        if (string.IsNullOrEmpty(tableName)) return null;
        // Already parsed?
        var existing = _parsedTables.Values.FirstOrDefault(t =>
            string.Equals(t.TableName, tableName, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;

        lock (_bcTableIndexLock)
        {
            EnsureBcSymbolTableIndex();
            if (_bcSymbolTableIndex != null)
            {
                foreach (var (id, entry) in _bcSymbolTableIndex)
                {
                    if (!string.Equals(entry.Table.TableName, tableName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!_parsedTables.ContainsKey(id))
                    {
                        _parsedTables[id] = entry.Table;
                        Console.Error.WriteLine($"[RecordPatches] BcAppFallback: parsed table '{tableName}' ({id}) by name from {Path.GetFileName(entry.AppPath)}");
                    }
                    return _parsedTables[id];
                }
            }
        }
        return null;
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
            var asmInfo = !string.IsNullOrEmpty(asm.Location) && File.Exists(asm.Location)
                ? new FileInfo(asm.Location)
                : null;
            var suffix = asmInfo != null
                ? $"{asmInfo.Length:x}-{asmInfo.LastWriteTimeUtc.Ticks:x}"
                : Guid.NewGuid().ToString("N");
            var tempPath = Path.Combine(Path.GetTempPath(), $"al-runner-systemapp-{suffix}.app");
            if (!File.Exists(tempPath))
            {
                using var fs = File.Create(tempPath);
                stream.CopyTo(fs);
            }

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
    /// Walk every table the BC .app indexes discovered and materialise it in
    /// _parsedTables. Symbols are preferred; AL source is only a fallback.
    /// </summary>
    internal static void EagerParseAllBcAppTables()
    {
        lock (_bcTableIndexLock)
        {
            int parsedNow = 0;
            EnsureBcSymbolTableIndex();
            if (_bcSymbolTableIndex != null)
            {
                foreach (var (id, entry) in _bcSymbolTableIndex)
                {
                    if (_parsedTables.ContainsKey(id)) continue;
                    _parsedTables[id] = entry.Table;
                    parsedNow++;
                }
            }

            if (_bcSymbolTableIndex == null || _bcSymbolTableIndex.Count == 0)
            {
                EnsureBcTableIndex();
                if (_bcTableIndex != null)
                {
                    var alreadySeenSources = new HashSet<string>(ReferenceEqualityComparer.Instance);
                    foreach (var (id, entry) in _bcTableIndex)
                    {
                        if (_parsedTables.ContainsKey(id)) continue;
                        if (!alreadySeenSources.Add(entry.Source)) continue;
                        TryParseTableFile(entry.Source);
                        if (_parsedTables.ContainsKey(id)) parsedNow++;
                    }
                }
            }

            if (parsedNow > 0)
                Console.Error.WriteLine($"[RecordPatches] BcAppFallback: eager-parsed {parsedNow} BC table(s) into _parsedTables");
        }
    }

    private static void EnsureBcTableIndex()
    {
        if (_bcTableIndex != null) return;
        var idx = new Dictionary<int, (string, string)>();
        foreach (var appPath in _bcAppPaths)
        {
            IReadOnlyList<(string Name, string Source)> sources;
            try { sources = AlRunner.AppLoader.ExtractAl(appPath); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RecordPatches] BcAppFallback: ExtractAl failed for {Path.GetFileName(appPath)}: {ex.Message}");
                continue;
            }
            foreach (var (_, source) in sources)
            {
                if (source.IndexOf("table", StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (Match m in _rxAnyTableId.Matches(source))
                {
                    if (int.TryParse(m.Groups[1].Value, out int id) && !idx.ContainsKey(id))
                        idx[id] = (appPath, source);
                }
            }
        }
        _bcTableIndex = idx;
        if (idx.Count > 0)
            Console.Error.WriteLine($"[RecordPatches] BcAppFallback: indexed {idx.Count} AL-source table id(s) across {_bcAppPaths.Count} BC .app file(s)");
    }

    /// <summary>
    /// Look up a query's SymbolReference.json definition by id across all registered BC
    /// .app dependencies (and any bundle .app registered as a query-symbol source).
    /// Returns null when no registered .app carries that query — caller falls back to
    /// the null-metaquery behaviour rather than fabricating one.
    /// </summary>
    internal static BcAppSymbolCache.QuerySymbol? TryGetQuerySymbol(int queryId)
    {
        lock (_bcTableIndexLock)
        {
            EnsureBcSymbolQueryIndex();
            return _bcSymbolQueryIndex != null && _bcSymbolQueryIndex.TryGetValue(queryId, out var q) ? q : null;
        }
    }

    /// <summary>
    /// Register a loose SymbolReference.json file (NOT a .app) as a query-symbol source.
    /// Used for source-only bundles whose queries we just compiled in-process — the file
    /// carries the BC-compiler-assigned column ids that the emitted Query DLL calls
    /// GetColumnByNo with. Idempotent; invalidates the query index so it's re-read.
    /// </summary>
    public static void RegisterBundleQuerySymbolsJson(string jsonPath)
    {
        if (string.IsNullOrEmpty(jsonPath) || !File.Exists(jsonPath)) return;
        lock (_bcTableIndexLock)
        {
            if (!_bcQuerySymbolJsonPaths.Contains(jsonPath, StringComparer.OrdinalIgnoreCase))
                _bcQuerySymbolJsonPaths.Add(jsonPath);
            // Always invalidate: the file is overwritten each run, so re-read even if the
            // path was already registered.
            _bcSymbolQueryIndex = null;
        }
    }

    private static void EnsureBcSymbolQueryIndex()
    {
        if (_bcSymbolQueryIndex != null) return;
        var idx = new Dictionary<int, BcAppSymbolCache.QuerySymbol>();
        foreach (var appPath in _bcAppPaths)
        {
            try
            {
                foreach (var q in BcAppSymbolCache.Get(appPath).Queries)
                    if (!idx.ContainsKey(q.Id))
                        idx[q.Id] = q;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RecordPatches] BcAppFallback: query SymbolReference read failed for {Path.GetFileName(appPath)}: {ex.Message}");
            }
        }
        // Loose SymbolReference.json sources (the bundle's own freshly-compiled queries).
        // Registered AFTER .app sources but only filling gaps (ContainsKey guard), so a
        // prebuilt .app's authoritative ids always win.
        foreach (var jsonPath in _bcQuerySymbolJsonPaths)
        {
            try
            {
                foreach (var q in BcAppSymbolCache.GetFromJson(jsonPath).Queries)
                    if (!idx.ContainsKey(q.Id))
                        idx[q.Id] = q;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RecordPatches] BcAppFallback: query symbols.json read failed for {Path.GetFileName(jsonPath)}: {ex.Message}");
            }
        }
        _bcSymbolQueryIndex = idx;
        if (idx.Count > 0)
            Console.Error.WriteLine($"[RecordPatches] BcAppFallback: indexed {idx.Count} symbol query id(s) across {_bcAppPaths.Count} BC .app file(s)");
    }

    /// <summary>
    /// Resolve a table NAME (as used in a query dataitem's RelatedTable) to its table id,
    /// ensuring the table is also materialised in _parsedTables so its NCLMetaTable can be
    /// built for query column field-name resolution. Returns -1 if unknown.
    /// </summary>
    internal static int ResolveTableIdByName(string tableName)
    {
        if (string.IsNullOrEmpty(tableName)) return -1;
        // First check already-parsed tables (test-source tables + previously-faulted-in BC tables).
        foreach (var t in _parsedTables.Values)
            if (string.Equals(t.TableName, tableName, StringComparison.OrdinalIgnoreCase))
                return t.TableId;
        // Otherwise scan the BC symbol table index (BaseApp/SystemApp tables).
        lock (_bcTableIndexLock)
        {
            EnsureBcSymbolTableIndex();
            if (_bcSymbolTableIndex != null)
                foreach (var (id, entry) in _bcSymbolTableIndex)
                    if (string.Equals(entry.Table.TableName, tableName, StringComparison.OrdinalIgnoreCase))
                    {
                        _parsedTables.TryAdd(id, entry.Table); // make it available for metatable build
                        return id;
                    }
        }
        return -1;
    }

    private static void EnsureBcSymbolTableIndex()
    {
        if (_bcSymbolTableIndex != null) return;
        var idx = new Dictionary<int, (string, ParsedTable)>();
        foreach (var appPath in _bcAppPaths)
        {
            try
            {
                foreach (var table in BcAppSymbolCache.Get(appPath).Tables)
                    if (!idx.ContainsKey(table.TableId))
                        idx[table.TableId] = (appPath, table);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RecordPatches] BcAppFallback: SymbolReference read failed for {Path.GetFileName(appPath)}: {ex.Message}");
            }
        }
        _bcSymbolTableIndex = idx;
        if (idx.Count > 0)
            Console.Error.WriteLine($"[RecordPatches] BcAppFallback: indexed {idx.Count} symbol table id(s) across {_bcAppPaths.Count} BC .app file(s)");
        // Co-build the extension index whenever the table index is (re)built.
        EnsureBcSymbolExtensionIndex();
    }

    /// <summary>
    /// Merge tableextension fields from all registered BC .app SymbolReference.json files
    /// into <c>_parsedExtensionFields</c> and <c>_extensionIdsByBaseTable</c>.
    ///
    /// Must be called while holding <see cref="_bcTableIndexLock"/>.
    /// Only runs once per registration epoch; reset by <see cref="AddBcAppPath"/> and by
    /// <see cref="ResetForReload"/> (since _parsedExtensionFields is cleared on reload).
    ///
    /// Mirrors AlSourceParser.cs:246-260 for populating those dictionaries.
    ///
    /// De-duplicates by field id: precompiled BaseApp SymbolReference.json lists fields both
    /// in the base table's Tables[] entry AND in TableExtensions[].Fields. The merge skips
    /// fields already present in _parsedTables (if the base table has been populated) by
    /// checking the merged list at build time — see NclMetaTableBuilder's deduplicate block.
    /// </summary>
    private static void EnsureBcSymbolExtensionIndex()
    {
        if (_bcSymbolExtensionIndexBuilt) return;
        _bcSymbolExtensionIndexBuilt = true;

        int merged = 0;
        foreach (var appPath in _bcAppPaths)
        {
            try
            {
                foreach (var ext in BcAppSymbolCache.GetTableExtensions(appPath))
                {
                    if (string.IsNullOrEmpty(ext.TargetTableName)) continue;
                    var key = ext.TargetTableName.ToLowerInvariant();

                    // Append fields to _parsedExtensionFields — keep existing list from AL source.
                    // Also add the extension id to _extensionIdsByBaseTable so RegisterParsedTableExtensions
                    // can instantiate the precompiled TableExtension{id} CLR type (from dep .app DLLs)
                    // and wire its triggers. This mirrors the AL-source path (AlSourceParser.cs:257-260).
                    // Registration is guarded in RegisterParsedTableExtensions: malformed instances
                    // (ObjectId.ObjectNumber ≠ extId) and duplicates are skipped without crashing.
                    if (ext.ExtensionId > 0)
                    {
                        if (!_extensionIdsByBaseTable.TryGetValue(key, out var extIdList))
                            _extensionIdsByBaseTable[key] = extIdList = new List<int>();
                        if (!extIdList.Contains(ext.ExtensionId))
                            extIdList.Add(ext.ExtensionId);
                    }

                    if (!_parsedExtensionFields.TryGetValue(key, out var existing))
                        _parsedExtensionFields[key] = new List<ParsedField>(ext.Fields);
                    else
                    {
                        var existingIds = new HashSet<int>(existing.Select(f => f.FieldId));
                        foreach (var f in ext.Fields)
                            if (existingIds.Add(f.FieldId))
                                existing.Add(f);
                    }

                    merged++;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RecordPatches] BcAppFallback: extension index failed for {Path.GetFileName(appPath)}: {ex.Message}");
            }
        }

        if (merged > 0)
            Console.Error.WriteLine($"[RecordPatches] BcAppFallback: merged {merged} precompiled tableextension(s) into _parsedExtensionFields across {_bcAppPaths.Count} BC .app file(s)");
    }
}
