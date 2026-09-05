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
    // tableId → the Caption that table's .app declares, or null when it declares none.
    // SymbolReference.json records a table's Caption on its Objects[] entry, not on the
    // Tables[] entry _bcSymbolTableIndex is built from, so this is a second dictionary
    // filled from the same scan rather than another field on that tuple. Read by
    // ResolveTableCaption (RecordPatches.AlObjectCaptionParser.cs) so a PRECOMPILED
    // dependency table's declared caption reaches the NCLMetaTable the runner builds for
    // it, the same way a source-parsed table's does.
    private static Dictionary<int, string?>? _bcSymbolTableCaptions;
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
    /// Drop the PER-BUNDLE .app registrations and every index derived from them, so the next
    /// lookup rebuilds against what the incoming bundle registers and nothing else (#2755).
    /// Called only from <c>ResetForReload</c> (RecordPatches.cs); see the comment at that call
    /// site for why clearing the registered set is safe there and what it fixes.
    ///
    /// <para>The process-lifetime SystemApp registration is deliberately kept — see the inline
    /// comment. It is the one entry in <see cref="_bcAppPaths"/> that nothing re-adds.</para>
    /// Must be called while holding <see cref="_bcTableIndexLock"/>.
    /// </summary>
    private static void ClearPerBundleBcAppPaths()
    {
        // The SystemApp package is registered ONCE per process, by RegisterSystemAppPackage()
        // from RecordPatches.Register() (the engine bootstrap) — never by Program.cs's per-bundle
        // dep-register stage, and never again after a reload. Dropping it here would unregister
        // the AL source for every NCL-internal system table (RecordLink 2000000068, Field
        // 2000000041, Object 2000000038, ...) for the whole remaining life of a --server /
        // --watch process: ResetForReload also clears _parsedTables and _metaTableCache, so the
        // registered .app IS the only thing those tables can be rebuilt from on request 2.
        //
        // Everything else in the list is a per-bundle registration that Program.cs re-adds
        // immediately after the reset, so it goes.
        _bcAppPaths.RemoveAll(p =>
            !string.Equals(p, _systemAppTempPath, StringComparison.OrdinalIgnoreCase));
        InvalidateBcAppIndexes();
    }

    /// <summary>The process-lifetime SystemApp .app <see cref="ClearPerBundleBcAppPaths"/> keeps,
    /// or null when <see cref="RegisterSystemAppPackage"/> has not run or could not extract it.</summary>
    internal static string? SystemAppPackagePathForTests
    {
        get { lock (_bcTableIndexLock) return _systemAppTempPath; }
    }

    /// <summary>
    /// Drop every lazily-built BC .app index so the next lookup rebuilds them from
    /// <see cref="_bcAppPaths"/> from scratch — including <see cref="_bcSymbolExtensionIndexBuilt"/>,
    /// which is what actually re-triggers <see cref="EnsureBcSymbolExtensionIndex"/> (its only
    /// call site is inside <see cref="EnsureBcSymbolTableIndex"/>, gated by
    /// <c>_bcSymbolTableIndex != null</c> — so nulling the table index without ALSO resetting
    /// the extension-built flag would still short-circuit the merge).
    ///
    /// Two callers (<see cref="AddBcAppPath"/> and <see cref="ClearPerBundleBcAppPaths"/>), and
    /// #2478 was exactly this pair being out of sync: <see cref="AddBcAppPath"/>
    /// already invalidated all four fields inline when a NEW .app was registered; <c>ResetForReload</c>
    /// (RecordPatches.cs) independently reset only <c>_bcSymbolExtensionIndexBuilt</c>, leaving
    /// <c>_bcSymbolTableIndex</c> populated — so on a warm --server/--watch reload,
    /// EnsureBcSymbolTableIndex's own-index guard returned early forever, and the extension merge
    /// silently never ran again for the rest of the process's life. Routing both call sites
    /// through one method makes that divergence impossible to reintroduce.
    /// Must be called while holding <see cref="_bcTableIndexLock"/>.
    /// </summary>
    private static void InvalidateBcAppIndexes()
    {
        _bcTableIndex = null;
        _bcSymbolTableIndex = null;
        // Built in the same pass as _bcSymbolTableIndex and gated on it being null, so it
        // has to be dropped together with it or a warm --server/--watch reload would serve
        // the previous registration epoch's captions.
        _bcSymbolTableCaptions = null;
        _bcSymbolQueryIndex = null;
        _bcSymbolExtensionIndexBuilt = false;
    }

    /// <summary>
    /// A stable digest of the BC symbol sources registered in THIS process — the
    /// <see cref="_bcAppPaths"/> set with each entry's content hash, plus the paths of the
    /// loose query-symbol JSON files. Folded into the install-baseline cache key
    /// (<c>TestExecutor.CurrentInstallBaselineCacheKey</c>); see that method for why.
    ///
    /// <para>Why this exists (#2710). The install-baseline snapshot is the rows the Install
    /// triggers and Company-Initialize wrote, and they write them through table metadata
    /// built from exactly these registrations — <see cref="EnsureBcSymbolTableIndex"/> /
    /// <see cref="EnsureBcSymbolExtensionIndex"/> rebuild the table and table-extension
    /// indexes by walking <see cref="_bcAppPaths"/>. So the registered set is an INPUT to the
    /// snapshot, and until this line existed the key never named it. #2712 measured what the
    /// difference is worth: dropping 90 of 96 Base Application table extensions flipped 47
    /// Tests-SMB tests ("field 5912 cannot be found in the 'Customer' table") with an
    /// unchanged exit code.</para>
    ///
    /// <para>The set genuinely varies between two runs whose (dependency assemblies, runner
    /// build, BC version) are identical — the three terms the key did name:</para>
    /// <list type="bullet">
    /// <item><description><b>--server / --watch USED to accumulate it, and no longer do
    /// (#2755).</b> <see cref="_bcAppPaths"/> is process-global and nothing cleared it:
    /// <see cref="InvalidateBcAppIndexes"/> drops the DERIVED indexes so they rebuild FROM
    /// this list, and <c>ResetForReload</c> (the per-bundle reload path) called exactly that
    /// and nothing more. Meanwhile the key's only per-bundle term IS reset per bundle —
    /// <c>InstallTriggerRunner.ResetForNewBundle</c> clears <c>_depAssemblies</c>. Two writers
    /// of the same per-bundle state, one keeping the invariant and one not: the second bundle
    /// in a server process computed its snapshot against its own apps UNION every earlier
    /// bundle's, then persisted it under the key a fresh single-bundle process would look up.
    /// <c>ResetForReload</c> now calls <see cref="ClearPerBundleBcAppPaths"/>, so the two
    /// writers agree. This term stays load-bearing regardless: it is what made the divergence
    /// observable rather than silent, it still separates a bundle whose deps differ from
    /// another's, and it still catches the second bullet below.</description></item>
    /// <item><description><b><see cref="RegisterBundleSymbolApps"/> skips what it cannot
    /// read.</b> An unreadable bundle-root .app is skipped as a whole with a <c>[warn]</c>
    /// line and the run continues — which is the right call for an optional input, but it
    /// means a transiently unreadable file (a concurrent write, a package left half-written
    /// by a killed run) silently removes a symbol source without changing any other key
    /// term.</description></item>
    /// </list>
    ///
    /// <para>Order-independent (paths are sorted) because registration order is an artifact
    /// of dependency-resolution order, not of what was registered. Content-hashed for .app
    /// entries because two packages can share a path across runs and differ in bytes;
    /// PATH-only for the query-symbol JSONs, which are this run's own compiler output — their
    /// content is already determined by the bundle sources the AL-output key hashes, so
    /// hashing them here would only add churn. <c>ComputeAppContentHash</c> is memoized per
    /// path and every registered .app was already hashed by <see cref="AddBcAppPath"/>'s own
    /// <c>BcAppSymbolCache.Get</c> call, so this costs a dictionary lookup per entry.</para>
    /// </summary>
    internal static string RegisteredBcAppSymbolStateKey()
    {
        string[] apps;
        string[] queryJson;
        lock (_bcTableIndexLock)
        {
            apps = _bcAppPaths.ToArray();
            queryJson = _bcQuerySymbolJsonPaths.ToArray();
        }
        return ComputeBcAppSymbolStateKey(
            apps.Select(p => (p, DescribeParsedSymbolState(p))), queryJson);
    }

    /// <summary>
    /// What this run actually PARSED out of one .app: its content hash, plus the shape of the
    /// parse result (table count, table-extension count).
    ///
    /// <para><b>Why the content hash alone is not enough — a correction to #2753.</b> #2753 keyed
    /// the install-baseline on the registered .app set by (path, content hash), on the reasoning
    /// that identical bytes mean identical symbols. That is false for the one mechanism #2710's
    /// field incident most likely ran through. #2712 is a measured case of the SAME bytes parsing
    /// to a DIFFERENT result: an allocation failure part-way through Base Application's
    /// SymbolReference.json was swallowed and the partial index — 90 of 96 table extensions
    /// missing — was cached in memory while the process carried on. The install triggers then
    /// wrote their rows through that degraded metadata, and the snapshot was persisted complete
    /// and valid under a key that, hashing only the bytes, was byte-identical to a healthy run's.
    /// Every later run read the wrong snapshot back. That is the cross-process poisoning #2710
    /// reported, and #2753 did not close it.</para>
    ///
    /// <para>#2722 closed the known producer by making a partial table-extension parse fatal, so
    /// today this is defence in depth rather than the only guard. It is worth having because
    /// #2722 protects one parse surface, while this makes ANY future divergence between "these
    /// bytes" and "what we parsed out of them" produce a different key instead of a silent
    /// cross-process wrong answer — which is the whole lesson of #2710.</para>
    ///
    /// <para>Both reads are ordinarily process-cache hits, not fresh work: <c>AddBcAppPath</c>
    /// already read both surfaces to completion at registration (#2722's ordering), so by the time
    /// an install-baseline key is computed the answers are memoised.</para>
    ///
    /// <para><b>Two conditions this must survive rather than throw on</b>, both because this runs
    /// on the install-baseline key path and not at registration, where throwing would turn a
    /// tolerated condition into a hard failure of every run:</para>
    /// <list type="bullet">
    /// <item><description>A registered .app that has since VANISHED from disk.
    /// <see cref="EnsureBcSymbolTableIndex"/> handles exactly that today — a <c>[warn]</c> and the
    /// index is built without it — so the runner already treats it as survivable. It is a
    /// materially different symbol state (that app's tables are simply absent), so it earns its
    /// own distinct term. Checked explicitly rather than relying on
    /// <c>ComputeAppContentHash</c>'s missing-file "unknown": that helper memoises per path, so an
    /// app hashed while it existed keeps answering with the OLD hash after it disappears.</description></item>
    /// <item><description>Any other read failure. It gets a term naming the exception type.</description></item>
    /// </list>
    ///
    /// <para>Neither fallback is a silent default, which is the distinction that matters here:
    /// both produce a key that DIFFERS from every healthy state, so the effect is a cache MISS and
    /// a recompute. A sentinel that collided with a healthy key would rebuild the exact defect
    /// this method exists to close.</para>
    /// </summary>
    private static string DescribeParsedSymbolState(string appPath)
    {
        try
        {
            if (!File.Exists(appPath)) return "absent";
            var contentHash = BcAppSymbolCache.ComputeAppContentHash(appPath);
            var symbols = BcAppSymbolCache.Get(appPath);
            var extensions = BcAppSymbolCache.GetTableExtensions(appPath);
            return $"{contentHash}|t{symbols.Tables.Count}|x{extensions.Count}";
        }
        catch (Exception ex)
        {
            return "unreadable:" + ex.GetType().Name;
        }
    }

    /// <summary>
    /// Testable core of <see cref="RegisteredBcAppSymbolStateKey"/>: takes the registered
    /// entries explicitly so a test can vary the set, the order and the content hashes
    /// without having to drive the process-global registry (which has no unregister).
    /// Mirrors the same core/wrapper split <c>RunnerFingerprint.WriteKeyLines</c> and
    /// <c>NclCecilRewrite.ComputeCacheKeyCore</c> use.
    /// </summary>
    internal static string ComputeBcAppSymbolStateKey(
        IEnumerable<(string Path, string ContentHash)> apps, IEnumerable<string> querySymbolJsonPaths)
    {
        var appList = apps.ToList();
        var jsonList = querySymbolJsonPaths.ToList();
        if (appList.Count == 0 && jsonList.Count == 0) return "|bcapps:none";
        appList.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));
        jsonList.Sort(StringComparer.Ordinal);
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        void Feed(string s) => hash.AppendData(System.Text.Encoding.UTF8.GetBytes(s));
        foreach (var (path, contentHash) in appList)
        {
            Feed("app\n");
            Feed(path);
            Feed("\n");
            Feed(contentHash);
            Feed("\n");
        }
        foreach (var p in jsonList)
        {
            Feed("qjson\n");
            Feed(p);
            Feed("\n");
        }
        return "|bcapps:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Test seam: the registered .app paths, so a test can assert what
    /// <see cref="RegisteredBcAppSymbolStateKey"/> is digesting rather than inferring it
    /// from the hash alone.</summary>
    internal static IReadOnlyList<string> RegisteredBcAppPathsForTests()
    {
        lock (_bcTableIndexLock) return _bcAppPaths.ToArray();
    }

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
            if (_bcAppPaths.Contains(appPath, StringComparer.OrdinalIgnoreCase)) return;

            // #2712: read BOTH symbol surfaces to completion BEFORE the path is registered.
            // Registration happens in Program.cs before any test runs, so a failure here is
            // fatal to the run (Program.cs catches BcAppSymbolReadException and exits 1)
            // instead of surfacing later as "this table has no extensions" — which is how an
            // OutOfMemoryException parsing Base Application's SymbolReference.json under a
            // 1 GB heap limit turned into 47 plausible-looking test failures and exit 0.
            // Reading first also means a failed .app is never left in _bcAppPaths, so a warm
            // --server process is not poisoned for every later request. The table-extension
            // read doubles as a cache warm-up: EnsureBcSymbolExtensionIndex's later call is a
            // process-cache hit, so this costs nothing the first index build would not have.
            BcAppSymbolCache.AppSymbols symbols;
            try
            {
                symbols = BcAppSymbolCache.Get(appPath);
                BcAppSymbolCache.GetTableExtensions(appPath);
            }
            catch (Exception ex) when (ex is not AlRunner.Infrastructure.BcAppSymbolReadException)
            {
                throw new AlRunner.Infrastructure.BcAppSymbolReadException(appPath, "table symbols", ex);
            }

            _bcAppPaths.Add(appPath);
            // This is the ONLY live path by which a precompiled dependency's enums reach
            // AlEnumMetadataRegistry (AlEnumMetadataRegistry.RegisterFromAppPath, which
            // looks like it does the same job, has no callers). Every field the symbol
            // carries has to be passed here or it is simply absent at runtime: the
            // per-value Captions (#1775) and the enum-level DefaultImplementation /
            // UnknownValueImplementation fallbacks (#2306) were both being dropped,
            // which is why Base App enum 205 "Alt. Cust VAT Reg. Doc." could not be cast
            // to its interface.
            foreach (var enumSymbol in symbols.Enums)
                AlRunner.AlEnumMetadataRegistry.Register(
                    enumSymbol.Id,
                    enumSymbol.Name,
                    enumSymbol.Options.ToArray(),
                    enumSymbol.Indexes.ToArray(),
                    enumSymbol.Implementations.Select(i => i.ToArray()).ToArray(),
                    enumSymbol.Captions?.ToArray(),
                    enumSymbol.DefaultImplementations?.ToArray(),
                    enumSymbol.UnknownImplementations?.ToArray());
            // Invalidate the indexes so newly-added .app gets picked up on next miss.
            InvalidateBcAppIndexes();
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
                catch (Exception ex)
                {
                    // A bundle-root .app is optional (a source-only bundle has none), so an
                    // unreadable one is skipped as a WHOLE — AddBcAppPath reads to completion
                    // or throws, so there is no partial to skip with (#2712). `[warn]` is
                    // exempt from Log's default-verbosity filter; the previous bare catch
                    // said nothing at all.
                    Console.Error.WriteLine(
                        $"[warn] BcAppFallback: skipping bundle-root .app {Path.GetFileName(app)} — " +
                        $"its symbols could not be read: {ex.Message}");
                }
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
            // Parse the source slice that contains this table id. The table parser reads the
            // structural shape (fields, keys, properties it knows); the object-caption parser
            // is a separate extractor and owns the object's top-level Caption. This path is
            // lazy and per-id, so it does not go through ParseAllRegisteredSourceFiles, which
            // is where the two normally run together (#1903). Without the second call a table
            // reached only through this fallback would have its caption silently dropped.
            TryParseObjectCaptionFile(entry.Source);
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
            // #2967 — SCRATCH-DIR CLASSIFICATION: deliberately SHARED and content-addressed
            // (the suffix is the SystemApp DLL's length + mtime), so it must NOT become a
            // per-process path: every runner on the machine reuses one 6 MB extraction.
            //
            // It was UNSAFE as written, though, and this was the one site matching the
            // "reader sees a half-written file under a name that promises its content" shape.
            // `File.Create(tempPath)` published the name at zero bytes and the copy then ran
            // for ~6 MB, so any concurrent runner passing its own `File.Exists` check in that
            // window skipped the write and registered a TRUNCATED .app. BC reports that as
            // `AL1023: The package file ... is not valid`, attributed to the compilation
            // rather than to the package, so it fails the whole run.
            //
            // Publishing through a private temp name and one rename closes the window without
            // giving up the sharing. Zero-length is explicitly not usable, so a leftover from
            // a build that predates this — or from a process killed between Create and the
            // first write — is replaced rather than adopted forever.
            var tempPath = AlRunner.Infrastructure.SharedTempFile.PublishAtomically(
                Path.Combine(Path.GetTempPath(), $"al-runner-systemapp-{suffix}.app"),
                fs => stream.CopyTo(fs));

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
                        // Same pairing as the lazy per-id path above — see its comment.
                        TryParseObjectCaptionFile(entry.Source);
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
        var captions = new Dictionary<int, string?>();
        foreach (var appPath in _bcAppPaths)
        {
            // #2712: every path here passed AddBcAppPath's eager read, so the only way this
            // read can fail is the file having changed or vanished on disk since (a --watch
            // dependency rebuilt or removed between iterations; a test fixture's temp dir
            // deleted). Vanished: skip the .app as a WHOLE and say so on a channel that is on
            // by default. Present but unreadable: a typed failure that propagates — the
            // previous catch wrote a `[RecordPatches]`-tagged line that Log's default filter
            // dropped, and published a table index missing that .app's tables.
            if (!File.Exists(appPath))
            {
                Console.Error.WriteLine(
                    $"[warn] BcAppFallback: registered dependency .app is no longer on disk; its tables " +
                    $"and table extensions are not available to this run: {appPath}");
                continue;
            }
            BcAppSymbolCache.AppSymbols symbols;
            try { symbols = BcAppSymbolCache.Get(appPath); }
            catch (Exception ex) when (ex is not AlRunner.Infrastructure.BcAppSymbolReadException)
            {
                throw new AlRunner.Infrastructure.BcAppSymbolReadException(appPath, "table symbols", ex);
            }
            foreach (var table in symbols.Tables)
                if (!idx.ContainsKey(table.TableId))
                    idx[table.TableId] = (appPath, table);
            // Same first-app-wins rule as the table index above, so the two dictionaries
            // cannot disagree about which .app a given table id came from. TryAdd rather
            // than an indexer assignment: the value may legitimately be null (the table
            // declares no Caption), and null must still claim the id so a later .app's
            // same-id caption does not overwrite it.
            foreach (var obj in symbols.Objects)
                if (string.Equals(obj.Kind, "Table", StringComparison.OrdinalIgnoreCase))
                    captions.TryAdd(obj.Id, obj.Caption);
        }
        _bcSymbolTableIndex = idx;
        _bcSymbolTableCaptions = captions;
        if (idx.Count > 0)
            Console.Error.WriteLine($"[RecordPatches] BcAppFallback: indexed {idx.Count} symbol table id(s) across {_bcAppPaths.Count} BC .app file(s)");
        // Co-build the extension index whenever the table index is (re)built. If that fails,
        // unpublish the table index again: EnsureBcSymbolExtensionIndex's only call site is
        // this one, gated by `_bcSymbolTableIndex != null`, so a published table index with
        // the extension flag still false would short-circuit every later call and serve
        // tables with no extensions for the rest of the process — the #2478 shape, reached
        // through a failure instead of a reset. Unpublished, the next lookup retries and
        // fails the same loud way.
        try
        {
            EnsureBcSymbolExtensionIndex();
        }
        catch
        {
            _bcSymbolTableIndex = null;
            _bcSymbolTableCaptions = null;
            throw;
        }
    }

    /// <summary>
    /// Merge tableextension fields from all registered BC .app SymbolReference.json files
    /// into <c>_parsedExtensionFields</c> and <c>_extensionIdsByBaseTable</c>.
    ///
    /// Must be called while holding <see cref="_bcTableIndexLock"/>.
    /// Only runs once per registration epoch; reset by <see cref="AddBcAppPath"/> and by
    /// <see cref="ResetForReload"/> (since _parsedExtensionFields is cleared on reload).
    ///
    /// Mirrors AlSourceParser.cs's TryParseTableExtensionFile for populating those
    /// dictionaries; both funnel through the shared <see cref="MergeExtensionFields"/>
    /// helper, which also evicts any already-built NCLMetaTable for the base table — see
    /// #2126. Registration is guarded in RegisterParsedTableExtensions: malformed instances
    /// (ObjectId.ObjectNumber ≠ extId) and duplicates are skipped without crashing.
    ///
    /// De-duplicates by field id: precompiled BaseApp SymbolReference.json lists fields both
    /// in the base table's Tables[] entry AND in TableExtensions[].Fields. The merge skips
    /// fields already present in _parsedTables (if the base table has been populated) by
    /// checking the merged list at build time — see NclMetaTableBuilder's deduplicate block.
    /// </summary>
    private static void EnsureBcSymbolExtensionIndex()
    {
        if (_bcSymbolExtensionIndexBuilt) return;

        int merged = 0;
        foreach (var appPath in _bcAppPaths)
        {
            // Vanished from disk since registration: EnsureBcSymbolTableIndex (the only
            // caller, same pass) already warned and skipped this .app's tables; skip its
            // extensions the same way so the two indexes agree.
            if (!File.Exists(appPath)) continue;

            // No catch here (#2712). This used to swallow any failure into a
            // `[RecordPatches]`-tagged stderr line — dropped by Log's default-verbosity
            // filter — AFTER having already flagged the index as built, so a partial merge
            // was presented as the complete answer for the rest of the process.
            // GetTableExtensions either returns the complete list or throws
            // BcAppSymbolReadException; MergeExtensionFields de-duplicates by field id, so
            // a retried merge after a failure is safe.
            foreach (var ext in BcAppSymbolCache.GetTableExtensions(appPath))
            {
                if (string.IsNullOrEmpty(ext.TargetTableName)) continue;

                MergeExtensionFields(ext.TargetTableName, ext.ExtensionId, ext.Fields);
                merged++;
            }
        }

        // Only once every registered .app merged to completion. Setting this FIRST was half of
        // the bug: a failure part-way through left the flag true and the merge never re-ran.
        _bcSymbolExtensionIndexBuilt = true;

        if (merged > 0)
            Console.Error.WriteLine($"[RecordPatches] BcAppFallback: merged {merged} precompiled tableextension(s) into _parsedExtensionFields across {_bcAppPaths.Count} BC .app file(s)");
    }
}
