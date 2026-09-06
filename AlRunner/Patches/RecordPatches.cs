// RecordPatches.cs — Attempt A prototype: NavRecord redirection to BC's own TempTableDataProvider.
//
// Strategy:
//   1. Parse AL source files → MetaField/MetaKey/MetaTable (public data classes in Types.dll).
//   2. Call NCLMetaTable.CreateFromMetaTable (internal) via reflection → real NCLMetaTable.
//   3. Hook NavRecordHandle.CreateTarget → construct Record{ID} with real NCLMetaTable.
//   4. Hook NavSession.DataAccessSource getter → return skeleton DataAccessSource.
//   5. Hook DataAccessSource.GetDataAccessForTable → call CreateTempDataAccess on self.
//   6. Hook NavDatabase.CollationAwareStringComparer → return OrdinalIgnoreCase comparer.
//
// This file is a SPIKE — not production code. Goal: get ≥1 test in 02-record-operations to PASS.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

/// <summary>Builds real NCLMetaTable objects from AL source, bypassing NCLMetadata service.</summary>
public static partial class RecordPatches
{
    // Reflected BC types — populated by Register().
    private static Type? _tMetaTable;
    private static Type? _tMetaField;
    private static Type? _tMetaKey;
    private static Type? _tFieldMetadataRelation;
    private static Type? _tNavType;
    private static Type? _tFieldClass;
    // Microsoft.Dynamics.Nav.Types.Metadata.ObsoleteState — MetaField's obsoleteState ctor
    // param type (#1780). Bound in Register() alongside the other MetaField-adjacent types.
    private static Type? _tObsoleteState;
    private static Type? _tMetaCalcFormula;
    private static Type? _tMetaFilter;
    private static Type? _tMetaCondition;
    private static Type? _tMetaFieldRelation;
    private static Type? _tFilterType;
    private static Type? _tNCLMetaTable;
    private static MethodInfo? _mCreateFromMetaTable;
    private static MethodInfo? _mCreateForTempTable;
    private static FieldInfo? _fVatdcInstance;
    private static Type? _tDataAccessSource;
    private static MethodInfo? _mCreateTempDataAccess;
    private static MethodInfo? _mGetVirtualDataAccess;
    private static System.Reflection.PropertyInfo? _pNclMetaTableIsVirtualTable;
    private static Type? _tGlobalFilters;
    private static Type? _tNavDatabase;
    private static Type? _tCollationAwareStringComparer;
    private static Type? _tSqlSortingProperties;
    private static FieldInfo? _fNavDatabaseCollation;
    private static FieldInfo? _fNavDatabaseSqlSortingProperties;
    private static object? _sqlSortingProperties;     // pre-built SqlSortingProperties
    private static FieldInfo? _fSessionDataAccessSource;
    private static FieldInfo? _fDasSession;
    private static FieldInfo? _fDasGlobalFilters;
    private static FieldInfo? _fDasTableVersionTokens;
    private static FieldInfo? _fDasSessionTransactionManager;
    private static object? _skeletonSessionTransactionManager;
    private static FieldInfo? _fNavRecordHandleTemp;
    private static object? _skeletonDatabase;   // pre-built NavDatabase skeleton

    // TempTableDataProvider fields for manual construction (bypass session.Database in ctor)
    private static FieldInfo? _fTtdpNavSession;
    private static FieldInfo? _fTtdpTable;
    private static FieldInfo? _fTtdpComparer;
    private static FieldInfo? _fTtdpPrimaryKeySortingFields;
    private static PropertyInfo? _pNclMetaKeySortingFieldsWithPK;  // NCLMetaKey.SortingFieldsWithPrimaryKeyFields (internal)
    private static object? _collationComparer;   // pre-built CollationAwareStringComparer

    // CalcNumeric hook — iterates in-memory rows via Filter() + accumulates count/sum/avg.
    private static MethodInfo? _mTtdpFilter;               // TempTableDataProvider.Filter(int,FiltersAndMarks,MutableRecordBuffer,SortingFieldList,bool)
    private static ConstructorInfo? _ctorFieldDictionaryNavValue; // FieldDictionary<NavValue>(Tuple<INavFieldMetadata,NavValue>[])

    // Cache: tableId → NCLMetaTable built from AL source.
    private static readonly ConcurrentDictionary<int, object?> _metaTableCache = new();

    // Cache: (DataAccessSource, tableId) → DataAccess (with TempTableDataProvider).
    // BC's real GetDataAccessForTable returns one shared TenantDataAccess for all Normal
    // tables; that DataAccess is constructed once in the DataAccessSource ctor. Our skeleton
    // routes everything to TempTableDataProvider, which is a per-table thing — but it must
    // still be **the same** TempTableDataProvider for every call on a given table, so that
    // Insert in one Record variable becomes visible to FindFirst in another. Without this
    // cache every Record-instance creates a fresh empty in-memory store.
    private static readonly ConditionalWeakTable<object, ConcurrentDictionary<int, object>> _dataAccessByTable = new();

    // ── Temporary-record DataAccess registry (issue #2524) ───────────────────────────────
    // A `Record X temporary` gets its OWN DataAccess from the isTemporary branch of
    // GetDataAccessForTableCore, and its store must contain EXACTLY the rows AL inserted --
    // nothing the runner puts there behind AL's back. That branch already honours the
    // invariant by construction: it skips every virtual-table populate below it.
    //
    // The runner's virtual-table populates, however, do not all run at DataAccess-creation
    // time. Three of them re-populate at FIND time, from DataAccess_IsManagedFindRequest /
    // DataAccess_AggregatePermissionSetGuardForGet, keyed only on the table id of the request
    // -- which a temporary record's request carries just the same. Those paths therefore wrote
    // real metadata rows into a temporary record's private store (measured on BC 28.1: a
    // `Record "Field" temporary` holding one AL row went from Count = 1 to Count = 178 across a
    // single FindSet, and FindSet returned the injected `timestamp` row, "No." = 0, instead of
    // AL's; `Record "Aggregate Permission Set" temporary` went 1 -> 123 and returned SECURITY;
    // `Record Date temporary` with a closed "Period Start" filter went 1 -> 31).
    //
    // Membership here is the signal the find-time paths lacked: it says "this DataAccess belongs
    // to a `temporary` record". It is registered in exactly one place, the isTemporary branch of
    // GetDataAccessForTableCore, which is the whole funnel -- Ncl's
    // DataAccessSource.GetDataAccessForTable is Cecil-REPLACED by
    // NavDataAccessSource_GetDataAccessForTable (NclCecilRewrite.Records.cs), so no Record
    // acquires a DataAccess by another route.
    //
    // Weak, so a temporary record's DataAccess stays collectable with the record; the value is
    // an unused sentinel, membership is the whole signal (same shape as
    // BlobStoreIsolationPatches._databaseBackedProviders).
    private static readonly ConditionalWeakTable<object, object> _temporaryRecordDataAccess = new();
    private static readonly object _temporaryRecordSentinel = new();

    /// <summary>
    /// Whether <paramref name="dataAccess"/> was handed out for a <c>temporary</c> record, whose
    /// store holds exactly what AL wrote to it. Every runner-side virtual-table populate must
    /// check this before writing rows: a temporary instance of a virtual table is an ordinary
    /// in-memory table that merely borrows that table's SHAPE, and the service tier's virtual
    /// provider never sees it. See issue #2524.
    /// </summary>
    internal static bool IsTemporaryRecordDataAccess(object? dataAccess)
        => dataAccess != null && _temporaryRecordDataAccess.TryGetValue(dataAccess, out _);

    // Source directories scanned for AL table definitions.
    private static readonly List<string> _sourceDirs = new();

    // Parsed table schemas: tableId → (fields, pkFieldIds).
    private static readonly Dictionary<int, ParsedTable> _parsedTables = new();

    // Parsed tableextension fields: base-table-name (lowercased) → list of extra fields.
    private static readonly Dictionary<string, List<ParsedField>> _parsedExtensionFields = new();

    // Parsed tableextension object ids: base-table-name (lowercased) → tableextension object
    // ids extending it, in AL declaration order (= the order BC registers them, which the
    // trigger pipeline preserves). Used to instantiate the emitted TableExtension{id} CLR
    // types and register them on each record (record-level triggers + field-validate handler
    // dispatch). See RecordPatches.CreateObjectInstance.cs / WireFieldTriggerHandlers.
    internal static readonly Dictionary<string, List<int>> _extensionIdsByBaseTable = new();

    /// <summary>
    /// Merge <paramref name="fields"/> into <c>_parsedExtensionFields[baseTableName]</c>,
    /// record <paramref name="extensionId"/> in <c>_extensionIdsByBaseTable</c>, and evict
    /// any already-built NCLMetaTable for the base table so the next lookup rebuilds it with
    /// these fields merged in.
    ///
    /// This is the single writer both tableextension-field sources funnel through — the
    /// AL-source parser (<c>TryParseTableExtensionFile</c> in
    /// RecordPatches.AlSourceParser.cs) and the precompiled-.app symbol merge
    /// (<c>EnsureBcSymbolExtensionIndex</c> in RecordPatches.BcAppFallback.cs) — so the
    /// eviction happens exactly once, in one place, and any future third writer inherits it
    /// automatically instead of needing to remember to call it. See #2126: before this,
    /// only the AL-source path evicted, so a base table whose NCLMetaTable had already been
    /// materialized (e.g. referenced by AL source parsed earlier in the dependency graph)
    /// before EnsureBcSymbolExtensionIndex ran stayed frozen forever without the precompiled
    /// extension's fields.
    /// </summary>
    private static void MergeExtensionFields(string baseTableName, int extensionId, IEnumerable<ParsedField> fields)
    {
        if (string.IsNullOrEmpty(baseTableName)) return;
        var key = baseTableName.ToLowerInvariant();

        // De-dup by field id: the same extension can legitimately be scanned/merged more than
        // once (a dependency app's source dir registered both by its own suite AND by
        // sibling-source discovery, or a precompiled SymbolReference.json listing the same
        // field in both the base table's Tables[] entry and TableExtensions[].Fields — see
        // #1686 / #1711). A duplicated field id corrupts NCLMetaTable's positional field-count
        // arithmetic.
        if (!_parsedExtensionFields.TryGetValue(key, out var existing))
            _parsedExtensionFields[key] = new List<ParsedField>(fields);
        else
        {
            var existingIds = new HashSet<int>(existing.Select(f => f.FieldId));
            foreach (var f in fields)
                if (existingIds.Add(f.FieldId))
                    existing.Add(f);
        }

        if (extensionId > 0)
        {
            if (!_extensionIdsByBaseTable.TryGetValue(key, out var extIds))
                _extensionIdsByBaseTable[key] = extIds = new List<int>();
            if (!extIds.Contains(extensionId))
                extIds.Add(extensionId);
        }

        EvictCachedMetaTableForBaseTable(baseTableName);
    }

    /// <summary>
    /// A parsed table's own fields PLUS every field any tableextension has merged onto it
    /// (<see cref="_parsedExtensionFields"/>, keyed by table name — the extension may be
    /// AL-source-parsed in this bundle or a dependency's, or precompiled in a dependency
    /// .app; <see cref="MergeExtensionFields"/> is the single writer for both). De-duplicated
    /// by field id the same way <see cref="RecordPatches.NclMetaTableBuilder"/> does when it
    /// builds the runtime NCLMetaTable, so a control-binding lookup and the record's own
    /// field layout never disagree about which fields a table has (issue #2490: a TestPage
    /// control bound to an extension field threw <c>testpage-control-binding</c> because
    /// <see cref="GetPageControlFieldMap"/> and <see cref="TryResolveDependencyFieldId"/> each
    /// searched only <c>ParsedTable.Fields</c> — the base table's own declared fields — and
    /// never looked at <c>_parsedExtensionFields</c> at all, even though the record itself
    /// already carries the extension's fields via this same dictionary).
    /// </summary>
    internal static IEnumerable<ParsedField> GetAllFieldsIncludingExtensions(ParsedTable table)
    {
        if (!_parsedExtensionFields.TryGetValue(table.TableName.ToLowerInvariant(), out var extFields)
            || extFields.Count == 0)
            return table.Fields;

        var baseFieldIds = new HashSet<int>(table.Fields.Select(f => f.FieldId));
        var extFieldsNew = extFields.Where(f => !baseFieldIds.Contains(f.FieldId));
        return table.Fields.Concat(extFieldsNew);
    }

    // Set to true once Register() has been called.
    private static bool _registered;

    /// <summary>
    /// Drop all per-bundle parsed/built table &amp; sub-object metadata, the record
    /// CLR-type cache, the registered source dirs, and the in-memory row store so
    /// the SAME process can re-load an edited bundle of the same identity (server
    /// mode). Reflection handles and the installed hooks (<c>_registered</c>) are
    /// preserved. Re-run <see cref="AddSourceDir"/> + emit + SetTestAssembly after.
    /// See <see cref="BcRuntime.ResetForNewBundleReload"/> for the full reload
    /// contract and the field-schema-edit limitation.
    /// </summary>
    public static void ResetForReload()
    {
        _metaTableCache.Clear();
        _recordTypeCache.Clear();
        _parsedTables.Clear();
        _parsedExtensionFields.Clear();
        _extensionIdsByBaseTable.Clear();
        // #2478: must invalidate _bcSymbolTableIndex too, not just _bcSymbolExtensionIndexBuilt —
        // EnsureBcSymbolExtensionIndex's only call site is inside EnsureBcSymbolTableIndex, gated
        // by `_bcSymbolTableIndex != null`. Leaving that index populated made the flag reset above
        // a no-op forever: on request 2 of a warm --server/--watch process, EnsureBcSymbolTableIndex
        // short-circuited before ever reaching the extension merge again, so precompiled
        // tableextension fields silently vanished from every metatable from the second request on.
        // Shares InvalidateBcAppIndexes with AddBcAppPath (RecordPatches.BcAppFallback.cs) so the
        // two call sites can't drift apart again the way they did here.
        lock (_bcTableIndexLock)
        {
            // #2755: the REGISTERED set goes too, not just the indexes derived from it.
            // InvalidateBcAppIndexes drops the derived table/extension indexes so the next lookup
            // rebuilds them FROM _bcAppPaths — so leaving that list populated meant bundle 2 in a
            // --server/--watch process rebuilt against its own registrations UNION every earlier
            // bundle's, while a fresh single-bundle process running bundle 2 alone saw only its
            // own. The neighbouring per-bundle state already held this invariant
            // (InstallTriggerRunner.ResetForNewBundle clears _depAssemblies), and the server
            // path's own comment states the intent: "New bundle in the server session: replace
            // (not inherit) the install-trigger registrations".
            //
            // Safe for the PER-BUNDLE registrations because every caller re-registers
            // immediately afterwards, and registers the FULL resolved closure rather than a
            // delta — platform and Base Application .apps included. ResetForNewBundleReload
            // (BcRuntime.cs, the single caller of this method) runs at Program.cs 2196 on the
            // CLI path and 4049 on the server path; the matching registrations are at 2354/2357
            // and 4533/4534, both AFTER. A clear therefore removes nothing the current bundle
            // does not immediately put back.
            //
            // NOT safe for all of them, which is why this is ClearPerBundleBcAppPaths and not
            // _bcAppPaths.Clear(): the SystemApp package is registered once per PROCESS, by
            // RegisterSystemAppPackage() from Register(), and no per-bundle path re-adds it. A
            // flat clear unregistered the AL source for every NCL-internal system table for the
            // rest of a --server/--watch process — and since this method also clears
            // _parsedTables and _metaTableCache, that registration is the only thing they can
            // be rebuilt from. Two AlRunner.Tests classes caught it as
            // "no NCLMetaTable for table N (dependency source not parsed)".
            //
            // #2478 is the reason this is spelled out rather than done quietly: the last defect
            // in this same reset path was an index reset that did not reset ENOUGH, and it made
            // precompiled tableextension fields vanish from every metatable from the second
            // server request on. That failure was silent; this one was too.
            //
            // Still accumulating, same shape, deliberately NOT changed here: the sibling list
            // _bcQuerySymbolJsonPaths (RecordPatches.BcAppFallback.cs), tracked in #2939. It
            // feeds _bcSymbolQueryIndex through the same derived/registered split and is the
            // other input to RegisteredBcAppSymbolStateKey. Left alone because #2755 scoped
            // itself to _bcAppPaths and called the blast radius out explicitly — and the
            // SystemApp regression above is what that caution was warning about.
            ClearPerBundleBcAppPaths();
        }
        // #3207: the object-reference const memo goes with them, and must be cleared HERE rather
        // than left to expire — it has no expiry. ResolveObjectIdByKindAndName memoises
        // (kind, name) -> id on top of ResolveTableIdByName / EnumerateKnownAlObjects, which read
        // _parsedTables and _bcSymbolTableIndex, i.e. exactly the state the three statements above
        // just discarded. Its doc comment justifies caching successes with "an id never changes
        // once known", and that holds INSIDE a bundle and not across a reload: bundle 2 of a
        // --server/--watch process declaring the same object name at a different id kept getting
        // bundle 1's, so the memo defeated an invalidation its own dependency performs. The
        // consequence is silent — a where() condition pinned to the wrong table id computes a
        // plausible wrong number rather than failing — which is the failure #3205's own comment
        // says it is avoiding, and the same shape as #2478 and #2755 in this same reset path.
        _objectRefConstIds.Clear();
        _fieldTriggersWiredTables.Clear();
        _parsedPages.Clear();
        _parsedPageExtensions.Clear();
        _parsedReports.Clear();
        _parsedReportExtensions.Clear();
        _parsedQueries.Clear();
        _parsedXmlPorts.Clear();
        _parsedObjectDecls.Clear();
        _parsedObjectCaptions.Clear();
        // Both keyed by (AppId, Name), both populated by the same per-file sweep
        // (ParseSourceFileIntoAllExtractors) as every dict above — an edited re-run that
        // renames or removes a profile/permission set must not keep serving the stale
        // declaration, the same reason _parsedTables/_parsedPages/_parsedObjectDecls are
        // cleared here. _parsedProfiles was missing this before #2357 — the same "current
        // bundle source" gap that left permission sets unattributed also left profiles
        // able to go stale across a --server reload.
        _parsedProfiles.Clear();
        _parsedPermissionSets.Clear();
        _metaFormCache.Clear();
        // #1957: the "already (successfully|un-)loaded" bookkeeping is a statement about
        // the NCLMetaForm instances _metaFormCache.Clear() just discarded — it must go
        // with them, or the next lookup short-circuits a brand-new skeleton as
        // "already loaded" and silently serves a control-less page. See
        // ResetPageMetadataForReload's doc comment for the full reasoning.
        ResetPageMetadataForReload();
        _metaReportCache.Clear();
        _metaQueryCache.Clear();
        _metaXmlPortCache.Clear();
        _sourceDirs.Clear();
        _installBaseline = null;
        SetActiveDepCompanyBaseline(null);
        _isolatedStorageBaseline = null;
        _recordLinkBaseline = null;
        _autoIncrementBaseline = null;
        // Drop the in-memory table rows so an edited re-run starts clean instead of
        // seeing Inserts from the previous run (which would e.g. throw "already exists").
        _dataAccessByTable.Clear();
        // _materialisationGates is deliberately NOT cleared here, and no reset path clears it.
        // A gate's latch names the storage INSTANCE it was set for, so clearing the map above
        // invalidates it on its own — see RecordPatches.TableMaterialisation.cs. Clearing the
        // gates as well would be a second mechanism for one fact, which is what let the fast
        // path trust a latch that no longer described anything (the reset paths that drop
        // storage are not all in one place, and only one of them knew to do it); worse, it
        // hands out fresh gate objects, so a reset racing a materialisation would put two
        // threads inside the create -> hydrate step under two different monitors. The gates are
        // a handful of empty objects keyed weakly by DataAccessSource, and they die with it.
        // Registered table connections cache one CrmTestDataProvider per table id, bound to
        // the previous run's NCLMetaTable — they go with the rows (#2725).
        TableConnectionPatches.ResetForReload();
    }

    public static void AddSourceDir(string dir) => AddSourceDirs(new[] { dir });

    /// <summary>
    /// Runs all eight source extractors (table, tableextension, page, report, query,
    /// xmlport, object-decl, object-caption) over ONE already-read file's text (#1903).
    /// <para>
    /// Before this, <see cref="AddSourceDirs"/>' per-file loop called all eight directly —
    /// each extractor is a thin foreach over <c>ParseAlObjects(text)</c>, which built its
    /// OWN full AL syntax tree from the same text. A source tree of N files therefore cost
    /// 8N parses of eight IDENTICAL trees, measured on a 7,339-file real-world corpus as
    /// ~59,000 parses / 29.7s per pass instead of 7,339 parses. <see cref="ParseAlObjects"/>
    /// now memoizes its most-recently-built tree keyed on (text, active preprocessor
    /// symbols) — see the comment there — so calling the eight extractors back-to-back on
    /// the SAME text, as both callers below do, costs one real parse plus seven cache hits.
    /// </para>
    /// <para>
    /// The (text, symbols) key is deliberate, not an oversight: #1900 was caused by a
    /// parser that stopped seeing <c>--define</c> symbols because a field FROZE at
    /// type-init before <c>BcCompiler.SetExtraPreprocessorSymbols</c> ran. A cache keyed on
    /// text alone would reintroduce that bug silently — two calls for the same text under
    /// different <c>--define</c> sets are a genuinely different parse, not a cache hit.
    /// </para>
    /// </summary>
    private static void ParseSourceFileIntoAllExtractors(string text, string? filePath = null)
    {
        TryParseTableFile(text);
        TryParseTableExtensionFile(text);
        TryParsePageFile(text);
        TryParseReportFile(text);
        TryParseQueryFile(text);
        TryParseXmlPortFile(text);
        TryParseObjectDeclFile(text);
        TryParseObjectCaptionFile(text);
        // Profiles need the file PATH, not just its text: a profile has no object id, and
        // its "All Profile" row carries the declaring app's id and name, which are only
        // knowable from the app.json that owns the file (#2317).
        TryParseProfileFile(text, filePath);
        // Permission sets need the file PATH for the same reason profiles do — their
        // "Metadata Permission Set" row carries the declaring app's id, only knowable from
        // the app.json that owns the file (#2357).
        TryParsePermissionSetFile(text, filePath);
    }

    /// <summary>
    /// The Register()-time equivalent of <see cref="AddSourceDirs"/>' per-file loop: one
    /// pass over every registered source dir, reading each file's text exactly once and
    /// running all eight extractors on it via <see cref="ParseSourceFileIntoAllExtractors"/>
    /// (#1903). This replaced seven independent sweeps (one per extractor kind), each of
    /// which re-walked every source dir and re-read every file from disk on its own — 7
    /// directory walks + 7 file reads + (with the old un-memoized parser) 8 tree builds per
    /// file, instead of 1 of each.
    /// </summary>
    private static void ParseAllRegisteredSourceFiles()
    {
        foreach (var dir in _sourceDirs)
            foreach (var file in AlRunner.Infrastructure.SafeDirectoryScan.Files(dir, "*.al"))
                ParseSourceFileIntoAllExtractors(File.ReadAllText(file), file);
    }

    /// <summary>
    /// Register N source dirs and populate the NCLMetadata cache ONCE for the whole
    /// batch, instead of once per dir (#1833). <see cref="AddSourceDir"/> delegates
    /// here with a single-element array so its per-call-populate semantics are
    /// unchanged for callers that add one dir at a time outside a loop (e.g. the
    /// sibling-dependency emit loop in Program.cs, which calls AddSourceDir for one
    /// dir at a time interleaved with other per-dep work and needs each dir's tables
    /// visible before the next dep's symbols.json is written).
    /// <para>
    /// <see cref="PopulateNclMetadataCache"/>'s own cost is driven by the TOTAL number
    /// of ids known so far (it rebuilds <c>_parsedTables.Keys.ToArray()</c> etc. and
    /// walks the whole set with an idempotent skip-if-cached check) — not by what a
    /// single dir contributed. Calling it once per dir in a loop of N dirs is
    /// therefore O(N) calls each doing O(total-ids-so-far) work: quadratic in N. This
    /// entry point parses every dir first, THEN calls it exactly once over the
    /// complete set — same total ids processed, but the "once per dir" work is
    /// eliminated (N calls -&gt; 1 call). Every AL source dir is still parsed exactly
    /// once (per the existing <see cref="_sourceDirs"/> de-dup below) and the cache is
    /// still guaranteed fully populated before this method returns, so any caller
    /// that reads the cache immediately afterward (as the register-source-dirs stage's
    /// caller does, before build-app-groups/emit/compile ever runs) sees every dir's
    /// metadata — new dirs are never silently dropped from the merge.
    /// </para>
    /// </summary>
    public static void AddSourceDirs(IEnumerable<string> dirs)
    {
        var parsedAny = false;
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            // De-dup: BuildSiblingSourceDeps (Program.cs) can legitimately call this for the
            // SAME dependency source dir twice — once while matching declared deps to sibling
            // source apps, once while emitting the synthetic workspace .app for a dep that
            // needs a fresh build. Without this guard the same dir lands twice in _sourceDirs,
            // so ParseAllRegisteredSourceFiles() parses its .al files twice on the next
            // Register()/rebuild. For a dependency that declares a `tableextension` on a
            // table whose base metadata comes from elsewhere (e.g. a platform-app table),
            // that duplicated every extension field id in _parsedExtensionFields — see #1686.
            // The dedup here is defense in depth alongside the field-id dedup in
            // TryParseTableExtensionFile.
            if (_sourceDirs.Contains(dir, StringComparer.OrdinalIgnoreCase)) continue;
            _sourceDirs.Add(dir);
            // If Register() already ran (it runs before the bucket loop), parse immediately.
            // The NCLMetadata cache is populated once below, after every dir in this batch
            // has been parsed — see the batching rationale on the doc comment above. Every
            // .al file's text is read ONCE and handed to all eight extractors together
            // (#1903) — see ParseSourceFileIntoAllExtractors.
            if (_registered)
            {
                var _diagBefore = ParseObjectTextCallCount;
                var _diagHitsBefore = ParseTreeCacheHitCount;
                var _diagFiles = 0;
                foreach (var file in AlRunner.Infrastructure.SafeDirectoryScan.Files(dir, "*.al"))
                {
                    _diagFiles++;
                    ParseSourceFileIntoAllExtractors(File.ReadAllText(file), file);
                }
                if (Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_PARSE_COUNTS") == "1")
                    Console.Error.WriteLine(
                        $"[parse-counts] dir files={_diagFiles} realParses={ParseObjectTextCallCount - _diagBefore} " +
                        $"treeCacheHits={ParseTreeCacheHitCount - _diagHitsBefore}");
                parsedAny = true;
            }
        }
        if (parsedAny)
            PopulateNclMetadataCache();
    }

    /// <summary>
    /// Reflect on the BC assemblies and build NCLMetaTable objects from any AL sources added so far.
    /// Must be called after ForceLoadBcDlls() but before any test runs.
    /// </summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;

        var typesAsm = AppDomain.CurrentDomain.GetAssemblies()
            .First(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types");
        var nclAsm = AppDomain.CurrentDomain.GetAssemblies()
            .First(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");

        // Data types (Microsoft.Dynamics.Nav.Types)
        _tMetaTable = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaTable")!;
        _tMetaField = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaField")!;
        _tMetaKey   = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaKey")!;
        _tFieldMetadataRelation = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.FieldMetadataRelation")!;
        _tNavType   = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.NavType")!;
        _tFieldClass = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.FieldClass")!;
        _tObsoleteState = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.ObsoleteState")!;
        _tMetaCalcFormula = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaCalcFormula")!;
        _tMetaFilter  = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaFilter")!;
        _tMetaCondition = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaCondition")!;
        _tMetaFieldRelation = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaFieldRelation")!;
        _tFilterType  = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.FilterType")!;

        // NCLMetaTable and factory (Microsoft.Dynamics.Nav.Runtime / Ncl)
        _tNCLMetaTable = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaTable")!;
        _mCreateFromMetaTable = _tNCLMetaTable.GetMethod("CreateFromMetaTable",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        // NCLMetadata.GetMetaTableById(int,bool,int), NCLMetadata.GetMetaApplicationObject
        // (ObjectType,int,bool,int and ApplicationObjectId,bool,int), and
        // NCLMetaTable.GetFieldByNo(extensionId,fieldNo) are all Cecil-owned (see
        // NclCecilRewrite.cs).

        // DataAccessTableVersionTokens.CreateForTempTable()
        var tDatv = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.DataAccessTableVersionTokens")!;
        _mCreateForTempTable = tDatv.GetMethod("CreateForTempTable",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

        // VirtualAndTempTransactionalDataCache.Instance
        var tVatdc = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.VirtualAndTempTransactionalDataCache")!;
        _fVatdcInstance = tVatdc.GetField("Instance",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

        // DataAccessSource and CreateTempDataAccess
        _tDataAccessSource = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.DataAccessSource")!;
        _mCreateTempDataAccess = _tDataAccessSource.GetMethod("CreateTempDataAccess",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        // GetVirtualDataAccess(NCLMetaTable) — BC's real router for virtual/system tables
        // (Field=2000000041 → FieldDataProvider, AllObj=2000000038 → AllObjDataProvider, …).
        // We re-route virtual tables here instead of dumping them into the empty temp store.
        _mGetVirtualDataAccess = _tDataAccessSource.GetMethod("GetVirtualDataAccess",
            BindingFlags.NonPublic | BindingFlags.Instance);
        _pNclMetaTableIsVirtualTable = nclAsm
            .GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaTable")!
            .GetProperty("IsVirtualTable", BindingFlags.Public | BindingFlags.Instance);

        // GlobalFilters (public ctor)
        _tGlobalFilters = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.GlobalFilters")!;

        // NavSession fields for DataAccessSource
        var tNavSession = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavSession")!;
        _fSessionDataAccessSource = tNavSession.GetField("<DataAccessSource>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance);

        // NavDatabase — skeleton instance (returned by NavSession.Database hook)
        _tNavDatabase = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavDatabase")!;
        _tCollationAwareStringComparer = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.CollationAwareStringComparer");
        _tSqlSortingProperties = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.SqlSortingProperties");
        _fNavDatabaseCollation = _tNavDatabase.GetField("collationAwareStringComparer",
            BindingFlags.NonPublic | BindingFlags.Instance);
        _fNavDatabaseSqlSortingProperties = _tNavDatabase.GetField("sqlSortingProperties",
            BindingFlags.NonPublic | BindingFlags.Instance);

        // Pre-build SqlSortingProperties so it's available for both the skeleton DB and the
        // NavSession.SortingProperties hook (used by RecordBufferComparer in TempTableDataProvider).
        _sqlSortingProperties = BuildSqlSortingProperties();

        // Build skeleton NavDatabase once — NavDatabase.CollationAwareStringComparer is JMP-hooked
        // so any non-null NavDatabase is sufficient; we just need it to not NRE.
        _skeletonDatabase = RuntimeHelpers.GetUninitializedObject(_tNavDatabase);
        if (_fNavDatabaseCollation != null && _tCollationAwareStringComparer != null)
        {
            var comparer = BuildCollationAwareComparer();
            if (comparer != null) _fNavDatabaseCollation.SetValue(_skeletonDatabase, comparer);
        }
        if (_fNavDatabaseSqlSortingProperties != null && _sqlSortingProperties != null)
            _fNavDatabaseSqlSortingProperties.SetValue(_skeletonDatabase, _sqlSortingProperties);

        // Populate sqlDatabaseProperties so NavDatabase.SqlDatabaseProperties returns a
        // non-null object. BaseApp telemetry (FeatureTelemetry.LogUsage → ALGetModuleInfo)
        // reads NavGlobal.AppDatabase.SqlDatabaseProperties.ApplicationFamily; on a skeleton
        // with no SQL there is no real ApplicationFamily, and the field defaults to "" —
        // a faithful empty-family value (telemetry-only, dropped as HTTP egress is OOS).
        var fSqlDbProps = _tNavDatabase.GetField("sqlDatabaseProperties",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var tSqlDbProps = _tNavDatabase.Assembly.GetType("Microsoft.Dynamics.Nav.Runtime.NavSqlDatabaseProperties");
        if (fSqlDbProps != null && tSqlDbProps != null)
        {
            // GetUninitializedObject skips field initializers, so applicationFamily and lockObj
            // are null and databasePropertiesReady is false. The ApplicationFamily getter calls
            // ReadDatabaseProperties(), which would Monitor.TryEnter(lockObj) (null → ArgNull) and
            // open a NavSqlConnectionScope (no SQL on the skeleton). Set databasePropertiesReady=true
            // and applicationFamily="" so the getter short-circuits to the empty family value —
            // the faithful "no SQL family known" result; telemetry is dropped (HTTP egress OOS).
            var sqlDbProps = RuntimeHelpers.GetUninitializedObject(tSqlDbProps);
            tSqlDbProps.GetField("applicationFamily", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.SetValue(sqlDbProps, string.Empty);
            tSqlDbProps.GetField("lockObj", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.SetValue(sqlDbProps, new object());
            tSqlDbProps.GetField("databasePropertiesReady", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.SetValue(sqlDbProps, true);
            // #2300: NavSqlDatabaseProperties.InvalidIdentifierChars is read by
            // NavSqlStatementHelper.ConvertToSqlIdentifier (via NCLMetaTable.SqlTableName),
            // which a Query with a FlowField column reaches while naming the FlowField's
            // synthesized sub-dataitem (NCLMetaQuery.CreateSubQueryForFlowFieldCalculation
            // → SqlTableDataProviderHelper.CreateDataItemFromFlowField). GetUninitializedObject
            // leaves the private `invalidIdentifierChars` field null, and ConvertToSqlIdentifier
            // iterates it unconditionally — NRE before any row is read, regardless of whether the
            // identifier it's naming actually contains an invalid character. Populate it from BC's
            // own internal constant (read via reflection, not restated as a literal, so a future
            // BC version's different default is picked up automatically rather than silently
            // diverging) — the same value the real ctor assigns before any SQL round-trip.
            var fDefaultInvalidChars = tSqlDbProps.GetField("DefaultInvalidIdentifierChars",
                BindingFlags.NonPublic | BindingFlags.Static);
            var defaultInvalidChars = fDefaultInvalidChars?.GetRawConstantValue() as string ?? ".\"\\/'%][";
            tSqlDbProps.GetField("invalidIdentifierChars", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.SetValue(sqlDbProps, defaultInvalidChars);
            fSqlDbProps.SetValue(_skeletonDatabase, sqlDbProps);
        }
        // companyTokens — BC's own NavDatabase ctor does `companyTokens = new CompanyTokens(this)`,
        // and GetUninitializedObject skips it. Anything that maps a company token back to a name
        // (NavMedia.MediaExists → Database.CompanyTokens.Get(ParentCompanyToken), and every other
        // company-scoped platform-table read) then NREs on the null. Build the real type through
        // its real constructor so its own field initializers run: companyNames starts as
        // { string.Empty }, i.e. token 0 == the runner's single unnamed company, which is exactly
        // what the rest of the runner uses (RecordImplementation.GetActiveCompany returns "").
        var fCompanyTokens = _tNavDatabase.GetField("companyTokens",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var tCompanyTokens = _tNavDatabase.Assembly.GetType("Microsoft.Dynamics.Nav.Runtime.CompanyTokens");
        if (fCompanyTokens != null && tCompanyTokens != null)
        {
            var ctorCompanyTokens = tCompanyTokens.GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null, new[] { _tNavDatabase }, modifiers: null);
            if (ctorCompanyTokens != null)
                AlRunner.Infrastructure.FieldPoke.SetInstance(fCompanyTokens, _skeletonDatabase,
                    ctorCompanyTokens.Invoke(new[] { _skeletonDatabase }));
        }

        // tenant — BC's own NavDatabase ctor takes the owning NavTenant and stores it in this
        // private field; GetUninitializedObject skips it, so NavDatabase.Tenant was null on the
        // skeleton even though NavSession.Tenant was not. Both NavSession.get_Database and
        // NavTenant.get_Database are Cecil-rewritten to hand out this one skeleton instance, so
        // the null was the answer every session got.
        //
        // What that broke: NavDatabase.UpgradeManager is
        //     upgradeManager ??= new NavDataUpgradeManager(SystemTenant.UpgradeMetadata, Tenant);
        // and the two-argument ctor chains through `tenant.Id` — the only dereference in its body,
        // since the workflow factory it also passes is a deferred lambda. So every caller of
        // NavSession.GetModuleExecutionContext / GetCurrentModuleExecutionContext died with a bare
        // NullReferenceException raised inside a BC ctor. That is reachable from ordinary AL:
        // BaseApp's Company-Initialize (codeunit 50) asks for the execution context from its
        // OnCompanyOpen subscriber, so any test that opens a company hit it. See AlRunner#2353.
        //
        // Note this is NOT the same method as NavSession.get_ExecutionContext, which
        // NclCecilRewrite already replaces with `return ExecutionContext.Normal` and whose comment
        // names this very NRE. That rewrite covers one method; the module-scoped siblings reach
        // Database.UpgradeManager through a different path and were left crashing.
        //
        // Populating the field rather than rewriting the property is what keeps the answer
        // faithful: BC's own GetModuleExecutionContext body still returns Install / Uninstall from
        // session.AppInstallationContext and Upgrade from session.AppUpgradeContext — both of which
        // the runner does populate while running install triggers — and only falls through to
        // Normal when no upgrade workflow is in progress, which on the skeleton is always
        // (GetUpgradeInformation answers NavWorkflowState.NotStarted when no workflow was started).
        // A blanket "return Normal" replacement would answer Normal inside an install trigger,
        // where real BC answers Install.
        var fDatabaseTenant = _tNavDatabase.GetField("tenant",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (fDatabaseTenant == null)
        {
            Console.Error.WriteLine(
                "[RecordPatches] NavDatabase.tenant field NOT FOUND — NavDatabase.Tenant stays null, "
                + "NavSession.GetModuleExecutionContext will NRE");
        }
        else if (fDatabaseTenant.GetValue(_skeletonDatabase) == null)
        {
            var skeletonTenant = AlRunner.BcRuntime.SkeletonSystemTenant;
            if (skeletonTenant != null)
            {
                AlRunner.Infrastructure.FieldPoke.SetInstance(fDatabaseTenant, _skeletonDatabase, skeletonTenant);
                Console.Error.WriteLine("[RecordPatches] Skeleton NavDatabase.tenant wired to the skeleton system tenant");
            }
            else
            {
                // BcRuntime.InjectSkeletonSystemTenant runs before ApplyRecordPatches, so this
                // is only reachable when the environment ctor fell back and left Tenants null.
                Console.Error.WriteLine(
                    "[RecordPatches] No skeleton system tenant available — NavDatabase.Tenant stays null");
            }
        }

        // NavDatabase.tableConnectionSettingsStorage — BC's TableConnectionManager reads it on
        // every RegisterTableConnection (#2725). See TableConnectionPatches.
        TableConnectionPatches.PlantTableConnectionSettingsStorage(_skeletonDatabase, _tNavDatabase);

        Console.Error.WriteLine($"[RecordPatches] Skeleton NavDatabase built: {_skeletonDatabase.GetType().Name}");

        // DataAccessSource fields to poke when creating skeleton
        _fDasSession = _tDataAccessSource.GetField("session",
            BindingFlags.NonPublic | BindingFlags.Instance);
        _fDasGlobalFilters = _tDataAccessSource.GetField("globalFilters",
            BindingFlags.NonPublic | BindingFlags.Instance);
        _fDasTableVersionTokens = _tDataAccessSource.GetField("tableVersionTokens",
            BindingFlags.NonPublic | BindingFlags.Instance);
        _fDasSessionTransactionManager = _tDataAccessSource.GetField("sessionTransactionManager",
            BindingFlags.NonPublic | BindingFlags.Instance);

        // Pre-build the singleton STM so all skeleton DAS instances share the same STM identity.
        _skeletonSessionTransactionManager = BuildSkeletonSessionTransactionManager();

        // TempTableDataProvider fields (for manual construction bypassing session.Database in ctor)
        var tTtdp = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.TempTableDataProvider")!;
        _fTtdpNavSession = tTtdp.GetField("navSession", BindingFlags.NonPublic | BindingFlags.Instance);
        _fTtdpTable = tTtdp.GetField("table", BindingFlags.NonPublic | BindingFlags.Instance);
        _fTtdpComparer = tTtdp.GetField("comparer", BindingFlags.NonPublic | BindingFlags.Instance);
        _fTtdpPrimaryKeySortingFields = tTtdp.GetField("primaryKeySortingFields",
            BindingFlags.NonPublic | BindingFlags.Instance);
        _mTtdpFilter = tTtdp.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "Filter" && m.GetParameters().Length == 5);
        if (_mTtdpFilter == null)
            Console.Error.WriteLine("[RecordPatches] WARN: TempTableDataProvider.Filter(5 params) not found");
        var tFieldDictGeneric = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.FieldDictionary`1");
        if (tFieldDictGeneric != null)
        {
            var tFieldDictNavValue = tFieldDictGeneric.MakeGenericType(typeof(NavValue));
            _ctorFieldDictionaryNavValue = tFieldDictNavValue.GetConstructors(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(c => c.GetParameters() is [{ ParameterType: { IsArray: true } }]);
            if (_ctorFieldDictionaryNavValue == null)
                Console.Error.WriteLine("[RecordPatches] WARN: FieldDictionary<NavValue>(Tuple[]) ctor not found");
        }

        // NCLMetaKey.SortingFieldsWithPrimaryKeyFields is internal — access via reflection
        var tNclMetaKey = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaKey")!;
        _pNclMetaKeySortingFieldsWithPK = tNclMetaKey.GetProperty("SortingFieldsWithPrimaryKeyFields",
            BindingFlags.NonPublic | BindingFlags.Instance);

        // Pre-build and cache the collation comparer
        _collationComparer = BuildCollationAwareComparer();
        Console.Error.WriteLine($"[RecordPatches] Collation comparer built: {_collationComparer?.GetType().Name ?? "null"}");

        // Parse AL source files (tables + tableextensions + pages + reports + queries +
        // xmlports + object decls (AllObj) + object captions (AllObjWithCaption)) — see
        // ParseAllRegisteredSourceFiles for why this is ONE pass over _sourceDirs rather
        // than seven (#1903).
        ParseAllRegisteredSourceFiles();

        // NCL-internal system tables (RecordLink=2000000068, Field=2000000041, …)
        // live as AL source embedded in Microsoft.BusinessCentral.SystemApp.dll's
        // SystemPackage stream. BC's own NCL code constructs records of those tables
        // directly via `new NavRecord(parent, id)` — bypassing our NavRecordHandle
        // patch — so their NCLMetaTable must be in NCLMetadata's cache dict before
        // any test runs. Eagerly parse them here so the populator below picks them up.
        RegisterSystemAppPackage();

        // §O: lazy-populate the skeleton NCLMetadata cache with one NCLMetaTable
        // per parsed table so NavGlobal.NCLMetadata.GetMetaTableById / Codeunit.Run
        // call sites find an entry instead of throwing
        // NavNCLApplicationObjectNotFoundException.
        PopulateNclMetadataCache();

        // NavRecordHandle private field 'temp'
        var tRecHandle = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavRecordHandle")!;
        _fNavRecordHandleTemp = tRecHandle.GetField("temp",
            BindingFlags.NonPublic | BindingFlags.Instance);
    }

    /// <summary>
    /// Replacement for NCLMetaTable.GetFieldByNo(int extensionObjectId, int fieldNo).
    /// BC compiled IL calls this overload for extension fields: e.g. GetFieldByNo(52800, 50100).
    /// Our skeleton NCLMetaTable has no extension objects registered, so the original throws
    /// NavNCLExtensionFieldNotFoundException. We fall back to TryGetFieldByNo(fieldNo, …) on
    /// the same instance, which succeeds because BuildNCLMetaTable already merged extension
    /// fields into allParsed (the base table's field list).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static NCLMetaField NCLMetaTable_GetFieldByNoExt(NCLMetaTable self, int extensionObjectId, int fieldNo)
    {
        if (self.TryGetFieldByNo(fieldNo, out NCLMetaField? f) && f != null)
            return f;
        // Extension field unknown to us — throw the same exception BC would throw.
        throw new InvalidOperationException(
            $"[RecordPatches] extension field {fieldNo} from extension {extensionObjectId} not found in NCLMetaTable {self.TableName}");
    }

    /// <summary>
    /// Replacement for NavSession.Database getter.
    /// NavSession.Database => Tenant.Database which requires a real tenant.
    /// Return the skeleton NavDatabase instead.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object? NavSession_get_Database(object self)
    {
        return _skeletonDatabase;
    }

    // Cached NavTenant.database (LazyEx<NavDatabase>) field, resolved lazily.
    private static FieldInfo? _fNavTenantDatabase;
    private static bool _fNavTenantDatabaseResolved;

    /// <summary>
    /// Replacement for NavTenant.get_Database. The real getter throws
    /// ArgumentNullException("NavDatabase") when the tenant's `database` LazyEx is null,
    /// which it always is on the skeleton (MetadataPatches leaves it null by design).
    /// Under R2R, `NavSession.Database => Tenant.Database` is inlined past our
    /// NavSession.get_Database redirect, so callers like ALNavApp.ALGetModuleInfo
    /// (FeatureTelemetry.LogUsage during Purch.-Post) reach NavTenant.Database directly.
    /// Return the runner's skeleton NavDatabase (which carries collation/sorting and an
    /// empty-family SqlDatabaseProperties) instead of throwing. If a real database LazyEx
    /// is ever present we honour it. Runtime-engine layer; faithful — the skeleton DOES
    /// have a minimal database, returning it is more correct than throwing.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object? NavTenant_get_Database(object self)
    {
        if (!_fNavTenantDatabaseResolved)
        {
            _fNavTenantDatabase = self?.GetType().GetField("database",
                BindingFlags.NonPublic | BindingFlags.Instance)
                ?? self?.GetType().BaseType?.GetField("database",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            _fNavTenantDatabaseResolved = true;
        }
        if (self != null && _fNavTenantDatabase != null)
        {
            var lazy = _fNavTenantDatabase.GetValue(self);
            if (lazy != null)
            {
                // LazyEx<NavDatabase>.Value — return the real DB if the tenant has one.
                var pValue = lazy.GetType().GetProperty("Value");
                var v = pValue?.GetValue(lazy);
                if (v != null) return v;
            }
        }
        return _skeletonDatabase;
    }

    /// <summary>
    /// Replacement for TempTableDataProvider.ctor(NavSession, NCLMetaTable).
    /// The real ctor calls navSession.Database.CollationAwareStringComparer which NREs on our
    /// skeleton session (no Tenant). We manually set all fields instead.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void TempTableDataProviderCtorReplacement(object self, NavSession session, NCLMetaTable table)
    {
        _fTtdpNavSession?.SetValue(self, session);
        _fTtdpTable?.SetValue(self, table);
        // NCLMetaKey.SortingFieldsWithPrimaryKeyFields is internal — use reflection
        var pkSortingFields = _pNclMetaKeySortingFieldsWithPK?.GetValue(table.PrimaryKey);
        _fTtdpPrimaryKeySortingFields?.SetValue(self, pkSortingFields);
        _fTtdpComparer?.SetValue(self, _collationComparer);
    }

    /// <summary>
    /// Replacement for TempTableDataProvider.CalcNumeric(CalcNumericProviderRequest).
    /// The real override throws NotSupportedException; this replacement iterates in-memory rows
    /// via the private Filter() helper and aggregates each requested FlowField.
    /// <para>#2937 — the doc comment here used to claim count/sum/average were "the only three
    /// calculation methods routed through CalcNumeric", and the result switch ended in
    /// <c>_ =&gt; sums[j]</c>, so Min/Max/Lookup/Exists/None were all answered with the SUM
    /// accumulator. For Min/Max nothing ever wrote that accumulator, so they came back as a
    /// constant 0 whatever the data — right for an empty source set only by coincidence.
    /// Count/Sum/Average is indeed what BC ROUTES here (DistinctSourceTable.AddField buckets
    /// Min/Max into MinMaxFlowFields → CalcMinMax, Lookup and Exists into their own lists), but
    /// "BC does not send it" is not a reason to answer it wrongly: Min/Max are now aggregated
    /// properly and everything else throws. Aggregation itself lives in
    /// <see cref="ComputeCalcNumericAggregate"/>.</para>
    /// <para>NegateResult (<c>CalcFormula = -sum(...)</c>) is applied here because BC applies it
    /// at this level too: NavSqlAggregateCommand's aggregate reader negates each aggregated
    /// value inside the provider, before the FieldDictionary is returned. The negation itself is
    /// BC's own NCLMetaCalculationFormula.NegateValue, reached through
    /// <see cref="FlowFieldPatches.NegateAggregateResult"/> so the two runner paths that negate
    /// a FlowField aggregate share one implementation (#1708, #2323).</para>
    /// <para>REACHABILITY, measured rather than assumed (#2937 left this open): instrumenting
    /// this method's result loop and running the whole al-language corpus (2496 tests) plus all
    /// of tests/runner-extras (264 tests) produced ZERO hits. FlowFieldPatches hooks
    /// NavRecord.CalcFieldsAsync ahead of FlowFieldsHelper, so ordinary CalcFields — on a
    /// temporary record too, since every runner table is backed by TempTableDataProvider — never
    /// arrives here. That is why the coverage for this method is C# (CalcNumericAggregateTests)
    /// and not AL: there is no AL shape today that reaches it, so an AL test would pass without
    /// executing a line of it.</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object TempTableDataProvider_CalcNumeric(object self, object request)
    {
        // #2648: a FlowField whose CalcFormula source is the Date virtual table reaches the
        // provider without ever going through DataAccess, so none of the Date window guards has
        // seen it. This call materialises the whole window on first such read (and is a
        // ConditionalWeakTable miss for every other table). It lives inside the replacement body
        // rather than as a Cecil prepend because Cecil REPLACES this method's body outright.
        //
        // #3044: the request goes with it, so a read whose "Period Start" filter is closed at
        // both ends AND already materialised does not re-materialise the whole window behind a
        // guard that had already narrowed it. Anything else — which is every FlowField shape
        // #2988 measured — still gets the window.
        EnsureDateStoreCoversProviderRequest(self, request);

        var rt = request.GetType();
        var companyToken   = (int)rt.GetProperty("CompanyToken")!.GetValue(request)!;
        var filtersAndMarks = rt.GetProperty("FiltersAndMarks")!.GetValue(request);
        var sourceTable    = (NCLMetaTable)rt.GetProperty("MetaApplicationObject")!.GetValue(request)!;
        var fieldsToCalc   = (FieldList)rt.GetProperty("FieldsToCalculate")!.GetValue(request)!;
        int fieldCount     = fieldsToCalc.Count;

        var primaryKeySortingFields = _fTtdpPrimaryKeySortingFields!.GetValue(self);
        int recordCount = 0;

        // Per-field state resolved once, not once per row: the calculation method, the source
        // field the formula names (Count has none), and the source values collected across the
        // matching rows. Only the value-consuming methods get a list — a Count field's slot
        // stays null and nothing is collected for it.
        //
        // Collecting the values rather than folding them into a running total is the same shape
        // RecordPatches.QueryProjection's ComputeAggregateCore uses, and it is what lets one
        // helper own Sum/Average/Min/Max instead of the result switch reading whichever
        // accumulator happened to be written. The cost is one NavValue REFERENCE per matching
        // row per aggregated field — the rows' own value objects, not copies, and the rows are
        // already materialised in the temp store by the Filter() call below.
        var methods = new NCLMetaCalculationMethod[fieldCount];
        var sourceFields = new NCLMetaField?[fieldCount];
        var sourceValues = new List<NavValue?>?[fieldCount];
        for (int i = 0; i < fieldCount; i++)
        {
            var field = (NCLMetaField)fieldsToCalc[i];
            methods[i] = field.CalculationFormula.CalculationMethod;
            if (methods[i] is NCLMetaCalculationMethod.Sum or NCLMetaCalculationMethod.Average
                or NCLMetaCalculationMethod.Min or NCLMetaCalculationMethod.Max)
            {
                sourceFields[i] = sourceTable.GetFieldByNo(field.CalculationFormula.FieldId, trapError: true);
                // Source field unresolvable → no values are collected and the aggregate answers
                // its empty-set value, which is what this method did before #2937 and what
                // FlowFieldPatches' own srcFieldColumn < 0 arm does. Left as-is deliberately:
                // making it loud is a change to the shared FlowField behaviour, not to this
                // method, and belongs with the sibling rather than half-applied here.
                if (sourceFields[i] != null) sourceValues[i] = new List<NavValue?>();
            }
        }

        var rows = (System.Collections.IEnumerable)_mTtdpFilter!.Invoke(
            self, new object?[] { companyToken, filtersAndMarks, null, primaryKeySortingFields, false })!;

        foreach (TempTableRecordBuffer row in rows)
        {
            checked { recordCount++; }
            for (int i = 0; i < fieldCount; i++)
            {
                var values = sourceValues[i];
                if (values == null) continue;
                values.Add(row[sourceFields[i]!.ColumnIndex]);
            }
        }

        var tuples = new Tuple<INavFieldMetadata, NavValue>[fieldCount];
        for (int j = 0; j < fieldCount; j++)
        {
            var field = (NCLMetaField)fieldsToCalc[j];
            var formula = field.CalculationFormula;

            // BC negates at this level too — see the method's doc comment. The negation is
            // passed IN rather than applied after the call so that "aggregate, then negate iff
            // NegateResult" is one decision with one owner, and so a test can drive it: BC's
            // own NCLMetaCalculationFormula.NegateValue resolves SourceField through the
            // metadata registry and therefore needs a live session, which a unit test does not
            // have. Exists never reaches the negation (ComputeCalcNumericAggregate throws for
            // it first), so the #2323 exist carve-out in FlowFieldPatches has no counterpart to
            // make here.
            var navValue = ComputeCalcNumericAggregate(
                methods[j], field, recordCount,
                (IEnumerable<NavValue?>?)sourceValues[j] ?? Array.Empty<NavValue?>(),
                $"{field.Parent?.TableName}.{field.FieldName}",
                formula.NegateResult,
                v => FlowFieldPatches.NegateAggregateResult(
                    formula, v, "TempTableDataProvider.CalcNumeric"));

            tuples[j] = new Tuple<INavFieldMetadata, NavValue>(field, navValue);
        }

        return _ctorFieldDictionaryNavValue!.Invoke(new object[] { tuples })!;
    }

    /// <summary>
    /// One CalcNumeric field's aggregate, over the source values of the rows that matched the
    /// formula's filters (<paramref name="rowCount"/> is how many rows matched — Count's answer,
    /// and Average's divisor, both of which count rows rather than non-null values).
    /// <para>Min/Max reuse <see cref="FlowFieldPatches.NavValueCompare"/> and
    /// <see cref="FlowFieldPatches.TypedDefaultForField"/> rather than re-deriving comparison or
    /// default semantics — the same reuse RecordPatches.QueryProjection's ComputeAggregateCore
    /// makes, so all three of the runner's aggregate paths order values and answer an empty set
    /// identically.</para>
    /// <para>The empty-source-set answers are deliberate, not a fallthrough: corpus PR
    /// StefanMaron/BusinessCentral.AL.Language.Tests#171 measured real BC on eight service tiers
    /// answering 0 for min/max/average over no matching rows — and 0D for a Date-typed one,
    /// which is why the answer is the field's OWN typed default and not a numeric zero.</para>
    /// <para>Anything else throws. BC never routes Exists/Lookup/None through CalcNumeric
    /// (DistinctSourceTable.AddField buckets them into their own field lists), so one arriving
    /// here means the dispatch changed — and per loud-failures.md that has to be loud rather
    /// than answered with a default. Min and Max are answered even though BC does not route
    /// them here either, because the aggregation is exactly the same work and a wrong constant
    /// was the #2937 defect.</para>
    /// </summary>
    /// <param name="negateResult">the formula's <c>NegateResult</c> — the leading minus in
    /// <c>CalcFormula = -sum(...)</c> (#1708).</param>
    /// <param name="negate">applies that minus, and is only ever called when
    /// <paramref name="negateResult"/> is true. Required rather than optional: a null default
    /// would let a caller silently answer the POSITIVE aggregate for a negated formula, which
    /// is the exact silent wrong value #1708 is about. Production passes
    /// <see cref="FlowFieldPatches.NegateAggregateResult"/>, so BC's own
    /// NCLMetaCalculationFormula.NegateValue stays the single owner of the negation.</param>
    internal static NavValue ComputeCalcNumericAggregate(
        NCLMetaCalculationMethod method,
        INavValueMetadata resultMetadata,
        int rowCount,
        IEnumerable<NavValue?> sourceValues,
        string fieldDescription,
        bool negateResult,
        Func<NavValue, NavValue> negate)
    {
        if (negateResult && negate == null)
            throw new ArgumentNullException(nameof(negate),
                $"NegateResult is set on '{fieldDescription}' but no negation was supplied — "
                + "answering the positive aggregate would be the silent wrong value of #1708");

        var aggregate = ComputeCalcNumericAggregateCore(
            method, resultMetadata, rowCount, sourceValues, fieldDescription);
        return negateResult ? negate(aggregate) : aggregate;
    }

    private static NavValue ComputeCalcNumericAggregateCore(
        NCLMetaCalculationMethod method,
        INavValueMetadata resultMetadata,
        int rowCount,
        IEnumerable<NavValue?> sourceValues,
        string fieldDescription)
    {
        switch (method)
        {
            case NCLMetaCalculationMethod.Count:
                return NavValue.CreateNavValueFromObject(resultMetadata, rowCount);

            case NCLMetaCalculationMethod.Sum:
            case NCLMetaCalculationMethod.Average:
            {
                Decimal18 sum = default;
                foreach (var v in sourceValues)
                {
                    if (v == null) continue;
                    sum = checked(sum + v.ToDecimal());
                }
                if (method == NCLMetaCalculationMethod.Sum)
                    // An empty sum is 0 by arithmetic, not by accumulator accident.
                    return NavValue.CreateNavValueFromObject(resultMetadata, sum);
                return rowCount > 0
                    ? NavValue.CreateNavValueFromObject(resultMetadata, sum / rowCount)
                    : EmptyAggregateDefault(resultMetadata);
            }

            case NCLMetaCalculationMethod.Min:
            case NCLMetaCalculationMethod.Max:
            {
                NavValue? best = null;
                foreach (var v in sourceValues)
                {
                    if (v == null) continue;
                    if (best == null
                        || (method == NCLMetaCalculationMethod.Min && FlowFieldPatches.NavValueCompare(v, best) < 0)
                        || (method == NCLMetaCalculationMethod.Max && FlowFieldPatches.NavValueCompare(v, best) > 0))
                        best = v;
                }
                return best ?? EmptyAggregateDefault(resultMetadata);
            }

            default:
                throw new RunnerOutOfScopeException(
                    "TempTableDataProvider.CalcNumeric",
                    $"not-yet-implemented — CalculationMethod {method} on '{fieldDescription}' "
                    + "is not aggregated by CalcNumeric. BC routes Exists to CalcExists and "
                    + "Lookup to CalcLookup (DistinctSourceTable.AddField), so a CalcNumeric "
                    + "request carrying one means the dispatch changed; answering it with the "
                    + "sum accumulator was issue #2937",
                    "todo");
        }
    }

    /// <summary>
    /// What an aggregate answers when no row contributed a value: the result field's OWN typed
    /// default (0 for Decimal/Integer, 0D for Date, …), never a bare numeric literal — same
    /// chain FlowFieldPatches' Min/Max/Lookup arms use.
    /// </summary>
    private static NavValue EmptyAggregateDefault(INavValueMetadata resultMetadata)
        => FlowFieldPatches.TypedDefaultForField(resultMetadata)
           ?? NavValue.CreateNavValueFromObject(resultMetadata, 0);

    /// Pre-populate the skeleton session's dataAccessSource field directly.
    /// NavSession.DataAccessSource getter is a trivial field return and gets inlined by JIT,
    /// so the JMP hook on it never fires. We must inject the DAS into the field directly.
    /// </summary>
    public static void InitializeSkeletonSession(object skeletonSession)
    {
        Console.Error.WriteLine($"[RecordPatches] InitializeSkeletonSession: _fSessionDataAccessSource={_fSessionDataAccessSource != null}, _tDataAccessSource={_tDataAccessSource != null}");
        if (_fSessionDataAccessSource == null || _tDataAccessSource == null) return;

        // If already set, nothing to do.
        var existing = _fSessionDataAccessSource.GetValue(skeletonSession);
        Console.Error.WriteLine($"[RecordPatches] existing DAS on session: {existing}");
        if (existing != null) return;

        // Ensure skeleton DB (needed by TempTableDataProvider ctor via navSession.Database.CollationAwareStringComparer)
        EnsureSkeletonDatabase(skeletonSession);

        // Build skeleton DataAccessSource
        var das = RuntimeHelpers.GetUninitializedObject(_tDataAccessSource);
        _fDasSession!.SetValue(das, skeletonSession);
        _fDasGlobalFilters!.SetValue(das, Activator.CreateInstance(_tGlobalFilters!));
        _fDasTableVersionTokens!.SetValue(das, _mCreateForTempTable!.Invoke(null, null));

        // Pre-populate sessionTransactionManager so the lazy-init path in
        // DataAccessSource.get_SessionTransactionManager (which would otherwise call
        // CreateAppDataAccess → CreateAppDataProvider → NRE) is bypassed. The skeleton STM
        // carries a single LogicalTransaction with TransactionType=Update so that the real
        // BC body of ALDatabase.get_ALCurrentTransactionType returns Update without machinery,
        // and SessionTransactionManager.AnyHasWriteTransactionStarted returns false (empty
        // transactionManagers dict — no per-table TM has ever begun a write transaction).
        // Faithful: the runner has no real write transaction system; Update is BC's default
        // for "browsing/reading without a lock-mode override", and IsInWriteTransaction is
        // observably false because nothing has called BeginTransaction.
        if (_skeletonSessionTransactionManager != null && _fDasSessionTransactionManager != null)
            _fDasSessionTransactionManager.SetValue(das, _skeletonSessionTransactionManager);

        // Inject directly into the session field (bypass the inlined getter)
        _fSessionDataAccessSource.SetValue(skeletonSession, das);
        Console.Error.WriteLine($"[RecordPatches] Skeleton DAS injected on session: {das.GetType().Name}");

        // Build a minimal NavSystemCodeunitFactory+GlobalTriggers on the skeleton company so that
        // NavRecord.IsGlobalTriggerImplemented doesn't NRE when it calls
        // Session.SystemCodeunitFactory.GlobalTriggers.GetTriggersOnTable().
        // The factory's GlobalTriggers.session is our skeleton which is not "IsCompanyOpen",
        // so GetTriggersOnTable() returns Triggers.None immediately.
        InjectSkeletonSystemCodeunitFactory(skeletonSession);

        // Populate the auto-property backing field for NavSession.ErrorCollection.
        // The real auto-property `internal ErrorCollection ErrorCollection { get; } = new ErrorCollection();`
        // is initialised in NavSession's instance ctor — which doesn't run for our
        // RuntimeHelpers.GetUninitializedObject skeleton. Without this, NavMethodScope.RunBehaviorAsync
        // NREs the moment any AL method tagged [ErrorBehavior(Collect)] runs (via
        // session.ErrorCollection.StartCollecting()). Construct the real ErrorCollection so
        // StartCollecting / StopCollecting / Collect all execute unmodified BC code (Option C —
        // reuse service-tier code per HANDOFF §2 invariant 4).
        InjectSkeletonErrorCollection(skeletonSession);
    }

    /// <summary>
    /// Build a skeleton SessionTransactionManager whose two read-only getters resolve to
    /// BC-faithful defaults for a session with no real write transaction:
    ///   * CurrentTransactionType => TransactionType.Update (BC's default lock mode)
    ///   * AnyHasWriteTransactionStarted => false (empty per-table TM dict)
    /// Built reflectively via RuntimeHelpers.GetUninitializedObject so we can skip the real
    /// ctor (which wires TransactionManagers tied to TenantDataAccess / AppDataAccess — both
    /// of which require a real connection in our skeleton). All fields the two getters read
    /// are populated explicitly; nothing else is touched.
    /// </summary>
    private static object? BuildSkeletonSessionTransactionManager()
    {
        var nclAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        var typesAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types");
        if (nclAsm == null || typesAsm == null) return null;

        var tStm = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.SessionTransactionManager");
        var tTm = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.TransactionManager");
        var tLt = tTm?.GetNestedType("LogicalTransaction", BindingFlags.NonPublic | BindingFlags.Public);
        var tTrType = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.TransactionType");
        if (tStm == null || tTm == null || tLt == null || tTrType == null) return null;

        // LogicalTransaction is a parameterless internal type whose backing fields are
        // exactly the auto-properties. Construct it and set TransactionType = Update (ordinal 1).
        var lt = Activator.CreateInstance(tLt, nonPublic: true);
        if (lt == null) return null;
        var fLtType = tLt.GetField("<TransactionType>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (fLtType == null) return null;
        var updateValue = Enum.ToObject(tTrType, 1); // TransactionType.Update
        fLtType.SetValue(lt, updateValue);

        // Build a TransactionManager via GetUninitializedObject and populate just the
        // logicalTransactions stack so get_CurrentTransactionType (which Peek()s the stack)
        // returns Update without going through TransactionalDataProvider / NavSession state.
        var tm = RuntimeHelpers.GetUninitializedObject(tTm);
        var fTmLogicalTransactions = tTm.GetField("logicalTransactions",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (fTmLogicalTransactions == null) return null;
        var stackType = typeof(Stack<>).MakeGenericType(tLt);
        var stack = Activator.CreateInstance(stackType)!;
        stackType.GetMethod("Push")!.Invoke(stack, new[] { lt });
        fTmLogicalTransactions.SetValue(tm, stack);

        // Build the SessionTransactionManager and populate:
        //   defaultTransactionManager → our skeleton TM (so STM.CurrentTransactionType works)
        //   transactionManagers       → empty dict (so AnyHasWriteTransactionStarted returns false)
        var stm = RuntimeHelpers.GetUninitializedObject(tStm);
        var fStmDefault = tStm.GetField("defaultTransactionManager",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var fStmDict = tStm.GetField("transactionManagers",
            BindingFlags.NonPublic | BindingFlags.Instance);
        fStmDefault?.SetValue(stm, tm);
        if (fStmDict != null)
        {
            // dict type: ConcurrentDictionary<TransactionManagerKey, TransactionManager>
            var dict = Activator.CreateInstance(fStmDict.FieldType);
            fStmDict.SetValue(stm, dict);
        }

        return stm;
    }

    private static void InjectSkeletonErrorCollection(object skeletonSession)
    {
        var nclAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        if (nclAsm == null) return;
        var tErrorCollection = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.ErrorCollection");
        if (tErrorCollection == null) return;

        var fErrorCollection = skeletonSession.GetType().GetField("<ErrorCollection>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (fErrorCollection == null)
        {
            Console.Error.WriteLine("[RecordPatches] ErrorCollection backing field not found");
            return;
        }
        // Already populated? leave alone.
        if (fErrorCollection.GetValue(skeletonSession) != null)
        {
            WireNavCurrentThreadSession(skeletonSession);
            return;
        }

        // ErrorCollection has only field initialisers (collectedErrors=null, currentCollectionScopeStart=-1)
        // and a default ctor; Activator.CreateInstance runs the field-initialiser block.
        var ec = Activator.CreateInstance(tErrorCollection, nonPublic: true);
        fErrorCollection.SetValue(skeletonSession, ec);
        Console.Error.WriteLine("[RecordPatches] Skeleton ErrorCollection injected");

        WireNavCurrentThreadSession(skeletonSession);
    }

    /// <summary>
    /// Wire NavCurrentThread.Session to return _skeletonSession. NavCurrentThread.Session reads
    /// NavThreadLocalStorage.Current.Session?.Target — an AsyncLocal&lt;IReference&lt;NavSession&gt;&gt;.
    /// Setting it on the bootstrap thread propagates via ExecutionContext into the test threads.
    /// Without this, ALIsCollectingErrors / ALHasCollectedErrors / ALClearCollectedErrors /
    /// ALGetCollectedErrors all dereference NavCurrentThread.Session (null) → NRE; the AL tests
    /// that rely on the [ErrorBehavior(Collect)] surface (CollectThenClear, ClearCollectedErrorsWorks,
    /// CollectMultipleErrors, etc.) all chain through these getters after the collect call returns.
    /// </summary>
    private static void WireNavCurrentThreadSession(object skeletonSession)
    {
        var nclAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        if (nclAsm == null) return;
        var tTLS = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavThreadLocalStorage");
        var tRef = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.Reference`1");
        var tNavSession = skeletonSession.GetType();
        if (tTLS == null || tRef == null) return;

        var pCurrent = tTLS.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
        var current = pCurrent?.GetValue(null);
        if (current == null) return;
        var pSession = tTLS.GetProperty("Session", BindingFlags.Public | BindingFlags.Instance);
        if (pSession == null) return;

        // Already set?
        if (pSession.GetValue(current) != null) return;

        // Build Reference<NavSession>(_skeletonSession). The single-arg ctor is public.
        var refClosed = tRef.MakeGenericType(tNavSession);
        var refInstance = Activator.CreateInstance(refClosed, new[] { skeletonSession });
        pSession.SetValue(current, refInstance);
        Console.Error.WriteLine("[RecordPatches] NavCurrentThread.Session wired to skeleton");
    }

    private static void InjectSkeletonSystemCodeunitFactory(object skeletonSession)
    {
        var nclAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        if (nclAsm == null) return;

        var tFactory = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavSystemCodeunitFactory");
        var tGlobalTriggers = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavSystemCodeunitGlobalTriggers");
        var tNavCompany = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavCompany");
        if (tFactory == null || tGlobalTriggers == null || tNavCompany == null) return;

        // Get the skeleton company from the session.
        var companyField = skeletonSession.GetType().GetField("company",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var skeletonCompany = companyField?.GetValue(skeletonSession);
        if (skeletonCompany == null) return;

        // Build the factory with the REAL public ctor when the skeleton session is a
        // TreeObject (it is — NavSession : TreeObject). A non-null `parent` makes the
        // lazy trigger getters (ReportingTriggers etc.) construct real
        // NavSystemCodeunit instances whose NavCodeunitHandle resolves through the
        // runner's CreateTarget hooks — required for the report-execution chain
        // (GetReportToRun → InvokeSubstituteReport, factory fork →
        // InvokeApplicationReportMergeStrategy, custom merger → OnCustomDocumentMergerEx).
        // Fall back to the old uninitialized skeleton if the ctor shape changed.
        object factory;
        var tTreeObject = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.TreeObject");
        var factoryCtor = tTreeObject != null
            ? tFactory.GetConstructor(new[] { tTreeObject })
            : null;
        if (factoryCtor != null && tTreeObject!.IsInstanceOfType(skeletonSession))
            factory = factoryCtor.Invoke(new[] { skeletonSession });
        else
            factory = RuntimeHelpers.GetUninitializedObject(tFactory);

        // Build GlobalTriggers with its REAL public ctor, NavSystemCodeunitGlobalTriggers(
        // TreeObject parent), parented to the session — same reasoning as the factory above.
        //
        // This used to be a GetUninitializedObject skeleton with `session` field-poked in.
        // That skipped the base NavSystemCodeunit ctor, which is what allocates
        // `codeunitHandle` — the handle BC's own NavGlobalTriggers.Insert/Modify/Delete/
        // RenameAsync invoke the "Global Triggers" codeunit (2000000002) through. Without it
        // no OnDatabase* / OnGlobal* event could ever be published, so AL subscribers to
        // Codeunit::"Global Triggers" silently never fired (corpus CU60210). It also skipped
        // the `triggersOnTables` field initializer, which the old code had to patch up by
        // hand; the real ctor does both correctly.
        object globalTriggers;
        var gtCtor = tGlobalTriggers.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Length == 1
                && c.GetParameters()[0].ParameterType.IsInstanceOfType(skeletonSession));
        if (gtCtor != null)
        {
            globalTriggers = gtCtor.Invoke(new[] { skeletonSession });
        }
        else
        {
            // Ncl shape changed. Fall back to the old skeleton rather than crash, but say so
            // loudly: on this path every global/database trigger stays silently undelivered.
            Console.Error.WriteLine(
                "[RecordPatches] WARN: NavSystemCodeunitGlobalTriggers(TreeObject) ctor not found — "
                + "falling back to an uninitialized skeleton; AL subscribers to "
                + "Codeunit::\"Global Triggers\" will not fire.");
            globalTriggers = RuntimeHelpers.GetUninitializedObject(tGlobalTriggers);
            var fSession = tGlobalTriggers.GetField("session",
                BindingFlags.NonPublic | BindingFlags.Instance);
            fSession?.SetValue(globalTriggers, skeletonSession);
        }

        // triggersOnTables: Dictionary<int, Triggers> — initialize empty so Monitor.TryEnter
        // doesn't NRE on the locked object. The dict is normally allocated via field initializer
        // which GetUninitializedObject skips.
        var fTriggersOnTables = tGlobalTriggers.GetField("triggersOnTables",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (fTriggersOnTables != null && fTriggersOnTables.GetValue(globalTriggers) == null)
        {
            var dictType = fTriggersOnTables.FieldType;          // Dictionary<int, Triggers>
            fTriggersOnTables.SetValue(globalTriggers, Activator.CreateInstance(dictType));
        }

        // GetTriggersOnTable is BC's own body again — it invokes GetDatabaseTableTriggerSetup
        // on the Global Triggers codeunit, whose AL subscribers decide the per-table mask.

        // Wire global triggers into factory.
        var fGlobalTriggers = tFactory.GetField("globalTriggers",
            BindingFlags.NonPublic | BindingFlags.Instance);
        fGlobalTriggers?.SetValue(factory, globalTriggers);

        // openedDialogRegistry — normally allocated by NavCompany's field initializer,
        // skipped by GetUninitializedObject. NavOpenDialogTracking (report execution:
        // RunReportInternalCoreAsync) pushes onto it → NRE without this.
        var tDialogRegistry = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavOpenedDialogRegistry");
        var fDialogRegistry = tNavCompany.GetField("openedDialogRegistry",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (tDialogRegistry != null && fDialogRegistry != null
            && fDialogRegistry.GetValue(skeletonCompany) == null)
        {
            fDialogRegistry.SetValue(skeletonCompany,
                Activator.CreateInstance(tDialogRegistry, nonPublic: true));
        }

        // The skeleton company has no Tree: it was produced by GetUninitializedObject, so it
        // never ran TreeObject's ctor and was never parented to anything. NavCompany.
        // RegisterFormInternal builds `new NavFormHandle(this, form)` with the COMPANY as
        // parent, and TreeHandler's ctor throws InvalidOperationException("Parent.Tree cannot
        // be null") for a parent with no tree — so registering a form (which BC does before
        // dispatching a modal page to its handler) failed outright.
        //
        // Parenting it to the session is where a real BC server puts it, and going through
        // BC's own CreateTreeHandler means the handler computes its own `session` from the
        // parent exactly as it would there.
        var fTreeObjTree = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.TreeObject")?
            .GetField("tree", BindingFlags.NonPublic | BindingFlags.Instance);
        if (fTreeObjTree != null && fTreeObjTree.GetValue(skeletonCompany) == null)
        {
            var createHandler = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.TreeHandler")?
                .GetMethod("CreateTreeHandler", BindingFlags.Public | BindingFlags.Static);
            if (createHandler != null)
            {
                var companyTree = createHandler.Invoke(null, new[] { skeletonSession, skeletonCompany });
                AlRunner.Infrastructure.FieldPoke.SetInstance(fTreeObjTree, skeletonCompany, companyTree!);
            }
            else
            {
                Console.Error.WriteLine(
                    "[RecordPatches] TreeHandler.CreateTreeHandler NOT FOUND — the skeleton company keeps "
                    + "a null Tree and RegisterForm will throw");
            }
        }

        // registeredForms — same class of gap as openedDialogRegistry above: allocated by
        // NavCompany's ctor (`registeredForms = new Dictionary<Guid, NavFormHandle>()`),
        // which GetUninitializedObject skips. BC's modal-page dispatch registers the form
        // before showing it and unregisters it in a finally, so `lock (registeredForms)`
        // threw ArgumentNullException from inside Monitor.Enter — surfacing to AL as a bare
        // "Value cannot be null." on the RunModal line, naming nothing.
        //
        // A real empty dictionary is the faithful value: a company that has no open forms
        // is exactly the runner's state, and BC populates and drains it itself from here on.
        var fRegisteredForms = tNavCompany.GetField("registeredForms",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (fRegisteredForms != null && fRegisteredForms.GetValue(skeletonCompany) == null)
            fRegisteredForms.SetValue(skeletonCompany,
                Activator.CreateInstance(fRegisteredForms.FieldType));

        // Inject factory into NavCompany.SystemCodeunitFactory auto-property backing field.
        var fFactory = tNavCompany.GetField("<SystemCodeunitFactory>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance);
        fFactory?.SetValue(skeletonCompany, factory);

        // W-8a PR1: populate NavCompany.trackChanges so NavRecord.InsertAsync's
        // `ParentCompany.TrackChanges.TrackChange(...)` call doesn't NRE on the getter.
        // We leave the internal `trackedChanges` dictionary null — TrackChange checks
        // `if (trackedChanges == null) return;` and exits cleanly.
        // (Ref: Ncl NavTrackChanges.TrackChange.)
        var tTrackChanges = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavTrackChanges");
        var fTrackChanges = tNavCompany.GetField("trackChanges",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (tTrackChanges != null && fTrackChanges != null && fTrackChanges.GetValue(skeletonCompany) == null)
        {
            // Prefer BC's REAL ctor, `NavTrackChanges(NavCompany parent)`. It parents the
            // object into the tree and allocates `trackedChanges`, both of which are now
            // required: NavCompany.RegisterFormInternal calls RegisterFormTableChanges, which
            // does `lock (trackedChanges)` with no null guard — unlike TrackChange, which is
            // why the GetUninitializedObject shell below was sufficient until form
            // registration started happening. It only became constructible once the company
            // itself got a Tree (above); before that its base ctor would have thrown.
            object? trackChanges = null;
            var realCtor = tTrackChanges.GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
                binder: null, types: new[] { tNavCompany }, modifiers: null);
            if (realCtor != null)
            {
                try { trackChanges = realCtor.Invoke(new[] { skeletonCompany }); }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
                    Console.Error.WriteLine(
                        $"[RecordPatches] NavTrackChanges ctor failed ({inner.GetType().Name}: {inner.Message}) "
                        + "— falling back to an uninitialized shell; form registration will throw");
                }
            }
            // Fallback keeps the previous behaviour rather than leaving the field null.
            trackChanges ??= RuntimeHelpers.GetUninitializedObject(tTrackChanges);
            fTrackChanges.SetValue(skeletonCompany, trackChanges);
        }
    }

    // ─── Hook Implementations ───────────────────────────────────────────────────

    /// <summary>
    /// Replacement for NavRecordHandle.CreateTarget():
    /// bypasses NCLMetadata by constructing Record{ID} directly with a real NCLMetaTable
    /// built from parsed AL source.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static NavRecord NavRecordHandle_CreateTarget(NavRecordHandle self)
    {
        int id = self.ObjectId.ObjectNumber;
        bool isTemp = _fNavRecordHandleTemp != null && (bool)(_fNavRecordHandleTemp.GetValue(self) ?? false);

        var metaTable = (NCLMetaTable?)_metaTableCache.GetOrAdd(id, BuildNCLMetaTable);
        if (metaTable == null)
        {
            if (id == 0)
                AlRunner.Infrastructure.RunnerScope.ThrowNotYetImplemented(
                    "NavRecord.CloneForVariant (default-variant tableId=0)",
                    "HANDOFF.md §6 row E — synthetic empty NavRecord for default-variant clone case");
            throw new InvalidOperationException(
                $"NavRecordHandle.CreateTarget: no NCLMetaTable for table {id} (AL source not parsed)");
        }

        // Find Record{ID} : NavRecord in the loaded test assembly.
        var recordType = FindRecordType(id);
        if (recordType == null)
            throw new InvalidOperationException(
                $"NavRecordHandle.CreateTarget: no loaded type Record{id} found");

        var ctor = recordType.GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Length == 6);
        if (ctor == null)
            throw new InvalidOperationException($"Record{id} has no 6-arg constructor");

        // Construct Record{ID}(parent, metaTable, isTemporary, sharedTable, companyName, securityFiltering)
        //
        // Validated, not Ignored: a Record variable created inside BC's test runner defaults to
        // SecurityFiltering.Validated — see corpus test
        // Codeunit60175.SecurityFiltering_Default_InTestContext_IsValidated_NotIgnored, which
        // asserts that contract explicitly. The distinction only became observable once
        // RecordImplementation.SetSecurityFiltering stopped being a no-op; before that the
        // argument passed here was discarded and the field kept its default.
        NavRecord rec;
        try
        {
            rec = (NavRecord)ctor.Invoke(new object?[] { self, metaTable, isTemp, null, null,
                SecurityFiltering.Validated });
        }
        catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException != null)
        {
            // BC's own CreateTarget goes through table.CreateObjectInstance, a compiled
            // factory, so an exception raised while the record binds — BC's "table connection
            // ... must be registered" for a TableType = CRM table with no connection (#2725),
            // or a RunnerOutOfScopeException from the data-access route — reaches the AL
            // `asserterror` as itself. ConstructorInfo.Invoke wraps it instead, and AL then
            // read "Exception has been thrown by the target of an invocation." as the error
            // text. Rethrow the real one with its stack intact.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;
        }
        StampObjectId(rec, id);

        // Register any tableextensions on this (primary) record instance so the extension's
        // record-level triggers and field-validate triggers dispatch. CreateObjectInstance
        // handles the xRec/OldRecord and subtable instances; this is the path the test's own
        // `Rec: Record "…"` variable comes through, which would otherwise have no extensions.
        RegisterParsedTableExtensions(rec, id);
        // Wire this table's field OnValidate/OnLookup handlers + field-validate subscribers onto its
        // (built+cached) metatable. Both matter for tables built lazily at runtime — e.g. a precompiled
        // BaseApp table whose metatable did not exist when the startup passes ran: without the field
        // wiring its OnValidate body never runs (e.g. Purchase Header."Buy-from Vendor No." copying the
        // vendor name), without the subscriber injection an ISV's OnAfterValidateEvent never fires.
        WireFieldTriggerHandlersForTable(id, metaTable);
        AlRunner.Patches.EventSubscriberPatches.InjectValidateSubsForTable(id, metaTable);
        // Table-level trigger subscribers (Insert/Modify/Delete/Rename ordinals) — same lazy
        // wiring, called here (after GetOrAdd returned) rather than from inside BuildNCLMetaTable
        // to avoid the reentrant-GetOrAdd stack overflow described on InjectTriggerSubsForTable.
        AlRunner.Patches.EventSubscriberPatches.InjectTriggerSubsForTable(id, metaTable);
        return rec;
    }

    /// <summary>
    /// Replacement for NavSession.DataAccessSource getter.
    /// Returns a skeleton DataAccessSource backed by TempTableDataProvider (in-memory).
    /// </summary>
    /// <remarks>
    /// PER RECORD CONSTRUCTION -- NavRecord..ctor asks the session for its DataAccessSource, so
    /// this getter runs once per AL record variable that comes into existence. It used to open
    /// with an unconditional Console.Error.WriteLine, the same leftover tracing aid the IsOpen
    /// hook carried, and CPU sampling of the test in issue 2304 caught the two of them the
    /// same way. Behind BcRuntime.HookTraceEnabled now, which is read once.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object? NavSession_get_DataAccessSource(NavSession self)
    {
        if (BcRuntime.HookTraceEnabled)
            Console.Error.WriteLine("[RecordPatches] NavSession_get_DataAccessSource called");
        // Return cached DataAccessSource stored on the session's field.
        var existing = _fSessionDataAccessSource?.GetValue(self);
        if (existing != null) return existing;

        // Ensure the session has a skeleton NavDatabase — needed by TempTableDataProvider ctor.
        EnsureSkeletonDatabase(self);

        // Build a skeleton DataAccessSource.
        var das = RuntimeHelpers.GetUninitializedObject(_tDataAccessSource!);
        _fDasSession!.SetValue(das, self);
        _fDasGlobalFilters!.SetValue(das, Activator.CreateInstance(_tGlobalFilters!));
        _fDasTableVersionTokens!.SetValue(das, _mCreateForTempTable!.Invoke(null, null));

        // Pre-populate sessionTransactionManager — see InitializeSkeletonSession for
        // the full rationale. Keeps DataAccessSource.get_SessionTransactionManager out
        // of CreateAppDataAccess → CreateAppDataProvider (which NREs on no real DB).
        if (_skeletonSessionTransactionManager != null && _fDasSessionTransactionManager != null)
            _fDasSessionTransactionManager.SetValue(das, _skeletonSessionTransactionManager);

        // Cache it on the session field.
        _fSessionDataAccessSource?.SetValue(self, das);
        return das;
    }

    private static void EnsureSkeletonDatabase(object session)
    {
        // _skeletonDatabase is pre-built in Register(); NavSession.Database is JMP-hooked to return it.
        // Nothing to inject on the session object itself.
    }

    private static object? BuildSqlSortingProperties()
    {
        if (_tSqlSortingProperties == null) return null;
        try
        {
            // SqlSortingProperties(CultureInfo culture, CompareOptions compareOptions, string collation)
            var sortingPropsCtor = _tSqlSortingProperties.GetConstructors(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(c => {
                    var ps = c.GetParameters();
                    return ps.Length == 3
                        && ps[0].ParameterType == typeof(System.Globalization.CultureInfo)
                        && ps[1].ParameterType == typeof(System.Globalization.CompareOptions);
                });
            if (sortingPropsCtor == null) return null;
            return sortingPropsCtor.Invoke(new object[] {
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.CompareOptions.IgnoreCase,
                "Latin1_General_CI_AS"
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RecordPatches] BuildSqlSortingProperties failed: {ex.Message}");
            return null;
        }
    }

    private static object? BuildCollationAwareComparer()
    {
        if (_tCollationAwareStringComparer == null || _tSqlSortingProperties == null) return null;
        var sortingProps = _sqlSortingProperties ?? BuildSqlSortingProperties();
        if (sortingProps == null) return null;
        try
        {
            var compCtor = _tCollationAwareStringComparer
                .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(c => c.GetParameters().Length == 1
                    && c.GetParameters()[0].ParameterType == _tSqlSortingProperties);
            return compCtor?.Invoke(new[] { sortingProps });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RecordPatches] BuildCollationAwareComparer failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Replacement for NavSession.get_SortingProperties — Database.SqlSortingProperties NREs because
    /// the skeleton NavDatabase does not have a collation set up for the lazy-init path. Return the
    /// pre-built SqlSortingProperties from RecordPatches.Register.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object? NavSession_get_SortingProperties(object self) => _sqlSortingProperties;

    /// <summary>
    /// Replacement for DataAccessSource.GetDataAccessForTable(NCLMetaTable, bool).
    /// Uses one shared in-memory provider per (DataAccessSource,table) for regular tables,
    /// but returns a fresh provider for temporary records so each temp variable has an
    /// isolated buffer unless explicitly shared by BC semantics.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    /// <summary>
    /// Clear the per-(DataAccessSource,table) DataAccess cache. Called between test
    /// invocations so each test starts with empty tables, mirroring BC's per-test
    /// isolation transaction. Without this the in-memory store accumulates state
    /// across tests and Insert calls hit duplicate-key errors on common identifiers.
    /// </summary>
    // TEMPORARY (memory-census diagnostic) — total DataAccessSource entries and
    // summed per-table DataAccess entry counts across all of them. See MemoryCensus.cs.
    internal static (int sources, int tables) CensusDataAccessByTable()
    {
        int sources = 0, tables = 0;
        foreach (var (_, perTable) in _dataAccessByTable)
        {
            sources++;
            tables += perTable.Count;
        }
        return (sources, tables);
    }

    public static void ResetPerTestState()
    {
        // ConditionalWeakTable doesn't support Clear directly; the simplest correct
        // approach is to drain the per-DataAccessSource dictionaries in place.
        // The DataAccessSource itself is cached on _skeletonSession's DataAccessSource
        // backing field (a single instance), so iterating known sources is sufficient.
        foreach (var (_, perTable) in _dataAccessByTable)
            perTable.Clear();

        // RecordLink polyfill store is also per-test — BC's RecordLink table is part
        // of the per-test transaction (records' links go away on rollback).
        AlRunner.Patches.RecordLinkPatches.ResetForTest();

        // IsolatedStorage in-memory store — per-test reset matches BC semantics where
        // a test's writes are rolled back on completion.
        AlRunner.Patches.TenantStoragePatches.ResetForTest();

        // MediaSet membership store — per-test reset matches BC semantics: a MediaSet
        // field's "Media Set" rows are as much part of the per-test transaction as any
        // other row, so they must not survive into the next test. See MediaSetPatches
        // file header (LIFETIME) for why this store needs an explicit reset instead of
        // relying on GC (the fix for #1773 keys it on a real, durable Guid rather than a
        // transient NavRecord instance, which is exactly what makes it durable — and
        // exactly why it needs this reset).
        AlRunner.Patches.MediaSetPatches.ResetForTest();

        // Write-transaction state behind Database.IsInWriteTransaction(). A test that
        // writes without committing must not leave the next test believing it started
        // inside a transaction — BC's per-test rollback ends the transaction either way.
        AlRunner.Patches.ALDatabasePatches.ResetWriteTransactionState();

        // Process-wide skeleton TreeSharedObjectContainer (SharedRecordRef / SharedNavStream
        // / SharedHttpRequest / SharedHttpResponseMessage / SharedNavHttpClient /
        // SharedNavObjectDictionary wrappers) — see BcRuntime.DisposeSkeletonSharedObjectContainerChildren
        // for why this is a distinct leak from _dataAccessByTable above and must be swept
        // at the same per-test boundary.
        AlRunner.BcRuntime.DisposeSkeletonSharedObjectContainerChildren();

        // SingleInstance=true codeunit instances are session-scoped in real BC and get reset
        // on the same per-test transaction rollback boundary as everything else above — without
        // this a SingleInstance codeunit's instance-variable state would leak from one test into
        // the next. See BcRuntime._singleInstanceCache / BcRuntime.ResetSingleInstanceCache.
        AlRunner.BcRuntime.ResetSingleInstanceCache();

        // Manual-binding event subscriptions (BindSubscription/Session.EventBindings) are
        // likewise a live-instance leak risk across the same boundary: a subscriber a test
        // bound and never unbound must not still be bound when the next test codeunit runs
        // (corpus-verified — TestEventManualBindingCrossCodeunit, 60244/60245 — a within-
        // codeunit leak across [Test] procedures IS faithful BC behaviour and is untouched
        // by this reset, since ResetPerTestState only runs at the codeunit/Test-isolation
        // boundary, not between methods sharing one codeunit instance). See #2466.
        AlRunner.BcRuntime.ResetEventBindingsForTestBoundary();
    }

    /// <summary>
    /// The --test-data on-demand load, installed by TestDataProvisioner.Arm() and null for
    /// every run that did not pass the flag (so a default run pays one null check per
    /// first-touch of a table). A delegate rather than a direct call because this file is the
    /// MECHANISM half: which tables a backup offers, and from which backup, is policy, and it
    /// lives in TestDataProvisioner. Arguments are (DataAccessSource, tableId).
    /// </summary>
    internal static Action<object, int>? TestDataOnDemandLoader;

    /// <summary>
    /// Told the id of a table whose storage was published from inside ANOTHER table's hydration
    /// and therefore could not be loaded there (#2877). Installed by TestDataProvisioner.Arm()
    /// alongside the loader, and null for every run without --test-data.
    ///
    /// The point is reporting, not mechanism: without it the provisioner has no record for that
    /// table at all, so TableOutcome answers null — which under the on-demand policy means
    /// "nothing in this run ever touched it", the opposite of what happened. Same argument as
    /// #2240.
    /// </summary>
    internal static Action<int>? TestDataDeferredLoadNotifier;

    /// <summary>
    /// Told (table id, reason) when a deferred load could not be run after all, because the
    /// store had rows by then or could not be read. Arriving here means the table ends the run
    /// WITHOUT its backup rows, so it is reported rather than skipped — a silent skip is what
    /// produced #2877. See .claude/rules/loud-failures.md.
    /// </summary>
    internal static Action<int, string>? TestDataDeferredLoadWriteOffNotifier;

    /// <summary>Re-entrancy depth for the loader — NOT a "which tables are loaded" cache,
    /// which #2262 rules out on purpose.
    ///
    /// Hydrating a table runs BC's own metadata and NavValue construction, and that code can
    /// reach a Record of ANOTHER table, which lands back in GetDataAccessForTableCore and
    /// would recurse.
    ///
    /// Skipping the nested load is only safe because the omission is RECORDED. The nested call
    /// has already published the nested table's storage by the time the load is refused, and
    /// "storage presence IS the have-we-loaded-this answer" then made the omission permanent:
    /// every later touch found the entry and never loaded it, so the table silently kept none
    /// of its backup rows for the whole run (#2877). GetOrCreateHydratedDataAccessCore's nested
    /// branch marks that instance as owing a load, and the next touch outside a materialisation
    /// settles it — see RecordPatches.TableMaterialisation.cs.</summary>
    [ThreadStatic] private static int _testDataLoadDepth;

    private static void InvokeTestDataOnDemandLoader(object source, int tableId)
    {
        var loader = TestDataOnDemandLoader;
        if (loader == null || _testDataLoadDepth > 0) return;
        _testDataLoadDepth++;
        try { loader(source, tableId); }
        finally { _testDataLoadDepth--; }
    }

    public static object NavDataAccessSource_GetDataAccessForTable(object self, NCLMetaTable table, bool isTemporary)
    {
        var dataAccess = GetDataAccessForTableCore(self, table, isTemporary);

        // Both branches below land on a TempTableDataProvider, so the provider alone
        // cannot say whether it is standing in for SQL or genuinely serving a
        // `temporary` record — and the two shapes disagree about whether an
        // uncommitted BLOB write reaches the stored row (corpus 60940, issue #1751).
        // This is the one place that still knows, so record it here.
        // A TableType = CRM table served by BC's CrmTestDataProvider is, in BC too, a
        // temp-token DataAccess over a TempTableDataProvider (CrmTableConnection.CreateDataAccess
        // passes DataAccessTableVersionTokens.CreateForTempTable) — not SQL-backed.
        if (!isTemporary && !TableConnectionPatches.IsExternalTableType(table, out _))
            BlobStoreIsolationPatches.MarkDatabaseBacked(dataAccess);

        return dataAccess;
    }

    private static object GetDataAccessForTableCore(object self, NCLMetaTable table, bool isTemporary)
    {
        try
        {
            if (isTemporary)
            {
                // A `temporary` record gets a fresh, private store and NONE of the
                // virtual-table populates below. Register it so the populates that run later
                // (at find/Get time, keyed only on table id) honour the same invariant this
                // early return does -- issue #2524.
                var tempDataAccess = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                _temporaryRecordDataAccess.AddOrUpdate(tempDataAccess, _temporaryRecordSentinel);
                return tempDataAccess;
            }

            // Per-(DataAccessSource, tableId) cache so Insert+Find on the same regular table
            // share storage.
            var perTable = _dataAccessByTable.GetValue(self,
                static _ => new ConcurrentDictionary<int, object>());
            var tableId = table.TableId;

            // ── External table types (CRM / ExternalSQL / Exchange / MicrosoftGraph) ─────
            // BC's own GetDataAccessForTable switches on table.TableType here and asks the
            // session's TableConnectionManager for the CURRENT connection of that type; the
            // connection builds the DataAccess. Every one of these used to fall through to the
            // temp store below as if it were a Normal table — a silent fake — because the
            // metadata layer mapped every TableType to Normal. With '@@test@@' registered the
            // CRM branch is BC's CrmTestDataProvider (Guid PK auto-assigned on insert); an
            // unregistered type is BC's own "not registered" error; a live connection is
            // refused by name. Not cached in perTable: the connection owns one provider per
            // table id itself (CrmTableConnection.testDataProviders), exactly as on a service
            // tier, and Unregister must drop it. See TableConnectionPatches (#2725).
            if (TableConnectionPatches.IsExternalTableType(table, out var externalTableType))
            {
                var externalSession = _fDasSession?.GetValue(self)
                    ?? throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                        $"Record {tableId} (TableType = {externalTableType})",
                        "table-connections — DataAccessSource has no skeleton session; see docs/scope.md",
                        "table-connections");
                return TableConnectionPatches.GetExternalDataAccess(
                    externalSession, table, externalTableType, _fDasGlobalFilters?.GetValue(self));
            }

            // ── Virtual Field system table (2000000041) ──────────────────────────────────
            // The Field table is virtual: the service tier computes its rows on the fly from
            // NCLMetadata (one row per NCLMetaField of the filtered TableNo) via the native
            // FieldDataProvider. Routing it to our empty in-memory store returns zero rows,
            // which makes BC code that iterates it (e.g. "Library - Workflow".EnableWorkflow,
            // Field.SetRange(TableNo,<t>); Field.FindSet()) throw "There is no Field within the
            // filter."
            //
            // The block below builds a managed Field-row provider: it populates our in-memory
            // store with REAL Field rows produced by BC's OWN managed row-builder
            // FieldDataProvider.GetFieldRecordBuffer (a pure NCLMetaField→NavValue[] projection,
            // NOT the crashing native find path) — see RecordPatches.FieldVirtualTable.cs. The
            // populate is faithful and works (hundreds of rows insert cleanly for every table).
            //
            // DEFAULT-ON. The subsequent `Field.FindSet()` is routed through a managed find
            // interception (a guard prepended to DataAccess.InnerFindAsync — see
            // RecordPatches.FieldFindIntercept.cs) that, for 2000000041 ONLY, runs the find
            // entirely in managed code (provider.Find → ResultSet → ResultSetEnumerator),
            // bypassing BC's native InnerFindAsync SQL transactional-cache prologue which AVs on
            // this virtual system table (its per-object SystemId/PK caches + table-version tokens
            // are never allocated — crash file-proven even with zero rows and TableType=Temporary).
            // The filtered TableNo's field rows are populated on demand at find time. Every other
            // table falls through to the original native InnerFindAsync unchanged.
            if (IsFieldVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var fieldDa))
                {
                    var created = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    fieldDa = perTable.GetOrAdd(tableId, created);
                }
                // Top up rows for every source table currently materialised (idempotent). The
                // subsequent Field.FindSet() is routed through DataAccess_FindAsync (a managed
                // bypass of BC's R2R InnerFindAsync, which AVs on this virtual system table) —
                // see RecordPatches.FieldFindIntercept.cs — and the filtered TableNo is populated
                // on demand there. This whole path is now DEFAULT-ON (no env gate): the find
                // interception means a populated Field table no longer crashes under R2R.
                var session = _fDasSession?.GetValue(self)
                    ?? throw FieldVirtualShapeGap("DataAccessSource has no skeleton session");
                PopulateFieldVirtualTable(fieldDa, table, session);
                return fieldDa;
            }

            // ── AllObj system virtual table (2000000038) ─────────────────────────────────
            // Also virtual on the service tier (AllObjDataProvider computes rows from
            // NCLMetadata.GetSnapshotOfAllObjects, whose body we Cecil-neuter because it
            // NREs on the skeleton NCLMetadata). Routed to the same in-memory store as
            // every other table, but POPULATED with one row per object the runner knows
            // about, so AllObj.Get(<type>, <id>) and filtered iteration answer truthfully
            // instead of always returning nothing.
            // See RecordPatches.AllObjVirtualTable.cs.
            if (IsAllObjVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var allObjDa))
                {
                    var createdAllObj = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    allObjDa = perTable.GetOrAdd(tableId, createdAllObj);
                }
                PopulateAllObjVirtualTable(allObjDa, table);
                return allObjDa;
            }

            // ── AllObjWithCaption system virtual table (2000000058) ──────────────────────
            // AllObj plus the Object Caption column, virtual for the same reason, and the
            // documented way for AL to render an object's caption (lookup pages bound to
            // it, TableRelation, and lookup(...) CalcFormula FlowFields). Same rows and
            // same key as AllObj — the inventory is literally shared, so the two tables
            // cannot disagree about which objects exist.
            // See RecordPatches.AllObjWithCaptionVirtualTable.cs.
            if (IsAllObjWithCaptionVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var allObjCaptionDa))
                {
                    var createdAllObjCaption = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    allObjCaptionDa = perTable.GetOrAdd(tableId, createdAllObjCaption);
                }
                PopulateAllObjWithCaptionVirtualTable(allObjCaptionDa, table);
                return allObjCaptionDa;
            }

            // ── Integer system virtual table (2000000026) ────────────────────────────────
            // Virtual on the service tier (IntegerDataProvider computes rows per Number on
            // demand). Routed to the same in-memory store as every other table and populated
            // over a bounded window, so `dataitem(X; Integer)` report datasets and plain
            // `Record Integer` iteration yield rows instead of silently yielding nothing.
            // See RecordPatches.IntegerVirtualTable.cs.
            if (IsIntegerVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var integerDa))
                {
                    var createdInteger = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    integerDa = perTable.GetOrAdd(tableId, createdInteger);
                }
                PopulateIntegerVirtualTable(integerDa, table);
                return integerDa;
            }

            // ── All Profile system virtual table (2000000178) ────────────────────────────
            // Virtual on the service tier too: AllProfileDataProvider's rows are every
            // profile every published app declares plus the tenant-owned ones. It is the
            // table Profile List / Profile Card are bound to and the one
            // Conf./Personalization Mgt. resolves a user's role centre through, so an empty
            // store made every read of it raise "There is no All Profile within the filter."
            // Populated once per provider — unlike AllObj this table is WRITTEN by AL, and a
            // top-up on a later handout would resurrect a just-deleted row.
            // See RecordPatches.AllProfileVirtualTable.cs.
            if (IsAllProfileVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var allProfileDa))
                {
                    var createdAllProfile = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    allProfileDa = perTable.GetOrAdd(tableId, createdAllProfile);
                }
                PopulateAllProfileVirtualTable(allProfileDa, table);
                return allProfileDa;
            }

            // ── Date system virtual table (2000000007) ───────────────────────────────────
            // Virtual on the service tier (DateDataProvider computes one row per period, for
            // each of the five period types, ON DEMAND, per request). Routed to the same
            // in-memory store as every other table, so `Record Date` iteration answers with
            // real periods instead of "There is no Date within the filter." Every piece of the
            // period arithmetic is BC's own code, called by reflection.
            //
            // NOTHING IS MATERIALISED HERE (#2648). This runs from
            // RecordImplementation.InitializeImpl — i.e. when the `Record Date` VARIABLE is
            // constructed, before any filter exists — so populating here meant inserting the
            // whole default window (1900-01-01..2099-12-31, 86,885 rows) whatever the caller
            // went on to ask for. A filter naming one week in 1850 cost ~109,000 row inserts to
            // return 7 rows. The three read paths (find, count, keyed Get) each carry the
            // request, so each populates exactly what its request can select; a request that
            // names no closed "Period Start" bound still gets the whole documented window,
            // because that is what answers it. See RecordPatches.DateVirtualTable.cs.
            if (IsDateVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var dateDa))
                {
                    var createdDate = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    dateDa = perTable.GetOrAdd(tableId, createdDate);
                }
                // The ninth Date refusal, and the only one outside
                // RecordPatches.DateVirtualTable.cs. It routes through that file's factory
                // rather than spelling the anchor itself, so the table cannot claim one thing
                // from the populator and another from this dispatch chain — the sibling defect
                // #2945 found for Field, Aggregate Permission Set and All Profile (#2965).
                //
                // The CHECK stays at handout even though the rows no longer are (#2648): the
                // skeleton session is what BC's own GetPeriodName needs to name a period, and a
                // DataAccessSource without one is a runner defect that must stay loud at exactly
                // the point it was loud before.
                var dateSession = _fDasSession?.GetValue(self)
                    ?? throw DateShapeGap(
                        "the DataAccessSource has no skeleton session, so BC's own "
                        + "DateDataProvider.GetPeriodName cannot name a period");
                PrepareDateVirtualTable(dateDa, table, dateSession);
                return dateDa;
            }

            // ── Report Layout List system virtual table (2000000234) ─────────────────────
            // Virtual on the service tier too (its rows are the layouts every published
            // app declares, plus tenant layouts). BC's own by-name layout resolution
            // (ReportLayoutSelection.GetLayoutByNameAndAppIDAsync) reads exactly this
            // table, so populating it from the compiler-captured `rendering { layout(…) }`
            // declarations makes selection-by-name work through BC's own code path.
            // See RecordPatches.ReportLayoutListVirtualTable.cs.
            if (IsReportLayoutListVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var layoutDa))
                {
                    var createdLayout = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    layoutDa = perTable.GetOrAdd(tableId, createdLayout);
                }
                PopulateReportLayoutListVirtualTable(layoutDa, table);
                return layoutDa;
            }

            // ── Report Metadata (2000000139) / Report Data Items (2000000203) ────────────
            // Virtual on the service tier too: their rows are computed from the metadata of
            // every published report. They are the documented way for AL to discover a
            // report's caption, request-page flag and dataset shape without running it, and
            // an empty store makes every such lookup answer "no such report" / "no data
            // items". See RecordPatches.ReportMetadataVirtualTable.cs.
            if (IsReportMetadataVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var reportMetaDa))
                {
                    var createdReportMeta = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    reportMetaDa = perTable.GetOrAdd(tableId, createdReportMeta);
                }
                PopulateReportMetadataVirtualTable(reportMetaDa, table);
                return reportMetaDa;
            }

            // ── Metadata Permission Set system virtual table (2000000250) ───────────────
            // Virtual on the service tier too (MetadataPermissionSetDataProvider computes
            // its rows from the permission sets the installed apps declare). An empty store
            // makes Microsoft's own "Users - Create Super User" (codeunit 9000) fail its
            // `MetadataPermissionSet.Get(<null guid>, 'SUPER')`, so every AL test that
            // creates a user dies before it starts (issue #2313).
            // See RecordPatches.MetadataPermissionSetVirtualTable.cs.
            if (IsMetadataPermissionSetVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var permSetDa))
                {
                    var createdPermSet = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    permSetDa = perTable.GetOrAdd(tableId, createdPermSet);
                }
                PopulateMetadataPermissionSetVirtualTable(permSetDa, table);
                return permSetDa;
            }

            // ── Aggregate Permission Set system virtual table (2000000167) ──────────────
            // Virtual on the service tier too: its rows are the UNION of System-scope
            // (Metadata Permission Set, 2000000250 — just above) and Tenant-scope (Tenant
            // Permission Set, 2000000165) rows, computed by BC's own
            // AggregatePermissionSetDataProvider, driven directly by reflection. An empty
            // store made every `Record "Aggregate Permission Set".Get(...)` fail "does not
            // exist" — the root of a 14-test "already bound" cascade in Microsoft's own
            // Tests-SINGLESERVER bucket (issue #2357, ruled out as a binding-mechanism
            // defect by #2393). See RecordPatches.AggregatePermissionSetVirtualTable.cs.
            if (IsAggregatePermissionSetVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var aggPermSetDa))
                {
                    var createdAggPermSet = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    aggPermSetDa = perTable.GetOrAdd(tableId, createdAggPermSet);
                }
                var aggPermSetSession = _fDasSession?.GetValue(self)
                    ?? throw AggregatePermissionSetShapeGap(
                        "DataAccessSource has no skeleton session, so BC's own "
                        + "AggregatePermissionSetDataProvider cannot be constructed");
                PopulateAggregatePermissionSetVirtualTable(aggPermSetDa, table, aggPermSetSession);
                return aggPermSetDa;
            }

            if (IsReportDataItemsVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var reportDiDa))
                {
                    var createdReportDi = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    reportDiDa = perTable.GetOrAdd(tableId, createdReportDi);
                }
                PopulateReportDataItemsVirtualTable(reportDiDa, table);
                return reportDiDa;
            }

            // ── Table Metadata (2000000136) ──────────────────────────────────────────────
            // Virtual on the service tier too: one row per table in the application. An
            // empty store makes every lookup answer "no such table", which is what broke
            // Base App "Page Management".GetDefaultLookupPageID on custom tables.
            // See RecordPatches.TableMetadataVirtualTable.cs.
            if (IsTableMetadataVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var tableMetaDa))
                {
                    var createdTableMeta = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    tableMetaDa = perTable.GetOrAdd(tableId, createdTableMeta);
                }
                PopulateTableMetadataVirtualTable(tableMetaDa, table);
                return tableMetaDa;
            }

            // ── Page Metadata (2000000138) ───────────────────────────────────────────────
            // Virtual on the service tier too: one row per page in the application. An
            // empty store makes every lookup answer "no such page", which is what broke
            // Base App "Page Management".GetDefaultCardPageID's SourceTable+PageType scan
            // fallback for tables declaring no LookupPageId. See
            // RecordPatches.PageMetadataVirtualTable.cs (#1769).
            if (IsPageMetadataVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var pageMetaDa))
                {
                    var createdPageMeta = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    pageMetaDa = perTable.GetOrAdd(tableId, createdPageMeta);
                }
                PopulatePageMetadataVirtualTable(pageMetaDa, table);
                return pageMetaDa;
            }

            // ── Time Zone (2000000164) ───────────────────────────────────────────────────
            // Virtual on the service tier too (TimeZoneDataProvider enumerates the HOST's
            // TimeZoneInfo.GetSystemTimeZones() and numbers them 1..N). An empty store made
            // every read answer "no such time zone" silently. The runner enumerates the same
            // host call BC does, which on Linux means IANA ids where a Windows-hosted tier
            // reports Windows ids — a deliberate, permanent divergence recorded in
            // docs/limitations.md. See RecordPatches.TimeZoneVirtualTable.cs (#2584).
            if (IsTimeZoneVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var timeZoneDa))
                {
                    var createdTimeZone = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    timeZoneDa = perTable.GetOrAdd(tableId, createdTimeZone);
                }
                PopulateTimeZoneVirtualTable(timeZoneDa, table);
                return timeZoneDa;
            }

            // ── Feature Key (2000000211) ─────────────────────────────────────────────────
            // Routed to BC's OWN FeatureKeyDataProvider: its feature list is a hardcoded static
            // in Microsoft.Dynamics.Nav.Types, so the rows are BC's rather than a second copy
            // that would drift. An empty store made Base Application's Feature Management read
            // every feature as unregistered and silently win the legacy code path.
            // NOT a claim about any specific feature's shipped state: the 28.1 set measured
            // here is 14 features, every one State = None. An earlier version of this comment
            // said CalcOnlyVisibleFlowFields ships AllUsers (ON); that was read off the wrong
            // Types.dll and is false for 28.1/28.4. Measure BuildFeatureKeys() against the
            // artifact under test before asserting any feature's state.
            // POPULATED READ-ONLY: real BC's Modify writes new state through to table
            // 2000000210, which is not implemented here, so a Modify would land in the temp
            // store and go nowhere. Issue #2585 tracks the write path.
            // See RecordPatches.FeatureKeyVirtualTable.cs (#2585).
            if (IsFeatureKeyVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var featureKeyDa))
                {
                    var createdFeatureKey = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    featureKeyDa = perTable.GetOrAdd(tableId, createdFeatureKey);
                }
                var featureKeySession = _fDasSession?.GetValue(self)
                    ?? throw FeatureKeyShapeGap(
                        "DataAccessSource has no skeleton session, so BC's own "
                        + "FeatureKeyDataProvider cannot be constructed");
                PopulateFeatureKeyVirtualTable(featureKeyDa, table, featureKeySession);
                return featureKeyDa;
            }

            // ── Windows Language (2000000045) ────────────────────────────────────────────
            // Virtual on the service tier too (WindowsLanguageDataProvider iterates BC's own
            // WindowsLanguageHelper.AllCultures). An empty store made every language lookup
            // answer "no such language" silently. The six license-derived columns and the four
            // installed-resource columns throw instead of guessing — see
            // RecordPatches.WindowsLanguageVirtualTable.cs (#2581).
            if (IsWindowsLanguageVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var windowsLanguageDa))
                {
                    var createdWindowsLanguage = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    windowsLanguageDa = perTable.GetOrAdd(tableId, createdWindowsLanguage);
                }
                PopulateWindowsLanguageVirtualTable(windowsLanguageDa, table);
                return windowsLanguageDa;
            }

            // ── CodeUnit Metadata (2000000137) ───────────────────────────────────────────
            // Virtual on the service tier too (CodeUnitDataProvider computes one row per
            // codeunit from NCLMetadata). An empty store makes every lookup answer "no such
            // codeunit", so Get() silently returns false and FindSet() raises — and a
            // TableRelation to this table refuses a codeunit that really is in the run.
            // The last missing member of the Table/Page/Report Metadata family above.
            // See RecordPatches.CodeunitMetadataVirtualTable.cs (#2544).
            if (IsCodeunitMetadataVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var codeunitMetaDa))
                {
                    var createdCodeunitMeta = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    codeunitMetaDa = perTable.GetOrAdd(tableId, createdCodeunitMeta);
                }
                PopulateCodeunitMetadataVirtualTable(codeunitMetaDa, table);
                return codeunitMetaDa;
            }

            // ── Object Metadata (2000000071) ─────────────────────────────────────────────
            // NOT a virtual table: a real application-database system table, read with plain
            // SQL by Ncl's own ObjectMetadataStorage. The runner has no application database,
            // so its store was empty and a FindLast raised "There is no Object Metadata
            // within the filter" — which is how it takes out Microsoft's own
            // Codeunit136608.VerifyValidatePackageCodeunitFailed (#2519).
            //
            // Because the table IS real, a --test-data backup can genuinely carry rows for it.
            // So the on-demand loader runs FIRST on a freshly created store, and the populator
            // below does nothing when the store already holds a row: real rows win, synthesis
            // is the fallback. Every other branch in this method serves a table no backup can
            // ever have rows for, which is why only this one loads before populating.
            //
            // That precedence needs the hand-out to be ordered as well as the load, or a second
            // thread is given the store between "created" and "hydrated", finds it empty and
            // synthesises over rows that are about to arrive (#2788). GetOrCreateHydratedDataAccess
            // is what guarantees it — see RecordPatches.TableMaterialisation.cs — so the populate
            // below always runs on a store that is either hydrated or never will be.
            // See RecordPatches.ObjectMetadataSystemTable.cs.
            //
            // A store published by a NESTED materialisation is the one case where the populate
            // must NOT run yet: it owes a --test-data load that could not run there, and
            // synthesising into it now would make that load a mix of real and synthesised rows.
            // MaterialiseObjectMetadataStore holds the populate off until the debt is settled
            // (#2877) — with no loader installed nothing is ever owed and this is unchanged.
            if (IsObjectMetadataSystemTable(table))
                return MaterialiseObjectMetadataStore(self, perTable, table, tableId);

            // ── Object (2000000001) ──────────────────────────────────────────────────────
            // The other half of the table relation Object Metadata."Object ID" declares
            // (TableRelation = Object.ID WHERE(Type = FIELD("Object Type"))). Also NOT a
            // virtual table: the legacy object registry, a real application-database SQL
            // table. Its store was empty, so every read answered "no such object" — silently,
            // because Microsoft has that field's TestTableRelation commented out (#2774).
            //
            // Its rows are an object INVENTORY, so unlike Object Metadata's fixed id list they
            // are projected from the same EnumerateKnownAlObjects that answers AllObj — which
            // is what stops the two tables disagreeing about which objects exist.
            //
            // Same --test-data precedence as Object Metadata directly above, for the same
            // reason (a restored backup can genuinely carry rows for a real SQL table), and
            // through the same ordered materialisation: GetOrCreateHydratedDataAccess is what
            // stops a second thread being handed this store between "created" and "hydrated",
            // finding it empty and synthesising over rows that are about to arrive (#2788).
            // See RecordPatches.ObjectSystemTable.cs and RecordPatches.TableMaterialisation.cs.
            if (IsObjectSystemTable(table))
            {
                var objectDa = GetOrCreateHydratedDataAccess(self, perTable, table, tableId);
                PopulateObjectSystemTable(objectDa, table);
                return objectDa;
            }

            // ── Page Control Field (2000000192) ──────────────────────────────────────────
            // Virtual on the service tier too: one row per field control declared on a
            // page, INCLUDING controls declared Visible = false. An empty store made every
            // filtered query answer "no rows" silently (no error), so a test asserting a
            // control is absent would have passed against a broken provider too. See
            // RecordPatches.PageControlFieldVirtualTable.cs (#1779).
            if (IsPageControlFieldVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var pageControlFieldDa))
                {
                    var createdPageControlField = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    pageControlFieldDa = perTable.GetOrAdd(tableId, createdPageControlField);
                }
                PopulatePageControlFieldVirtualTable(pageControlFieldDa, table);
                return pageControlFieldDa;
            }

            // ── --test-data on-demand load (#2262) ───────────────────────────────────────
            // A store that does not have this table yet is exactly when a --test-data run needs
            // its rows read out of the backup. Same choke point the virtual tables above use,
            // and for the same reason: it is the only place a table's storage is materialised,
            // so the load always lands before the operation that triggered it — a read and a
            // write are equally covered.
            //
            // Storage presence IS the "have we loaded this" answer, so there is no flag to
            // keep in step: RestoreInstallBaselineSnapshot repopulates perTable from exactly
            // the snapshot it restores, so a table the last restore carried is present and
            // does not reach the create below. See TestDataProvisioner's header.
            //
            // Only the GetOrAdd winner loads — a loser would be hydrating into storage it is
            // about to throw away — and no racer is handed the storage until that load has
            // finished, so a caller here can never act on a half-hydrated table (#2788).
            // See RecordPatches.TableMaterialisation.cs.
            return GetOrCreateHydratedDataAccess(self, perTable, table, tableId);
        }
        catch (Exception ex)
        {
            var inner = ex is System.Reflection.TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            // Header + trace in ONE tagged write: Log.FilteredWriter matches per write and
            // a bare stack trace has no `[Component]` tag, so a second call would print its
            // frames at default verbosity under a header the filter had just dropped.
            Console.Error.WriteLine(
                $"[RecordPatches] GetDataAccessForTable failed for table "
                + $"{table?.TableName ?? "null"}: {inner.GetType().Name}: {inner.Message}"
                + $"\n{inner.StackTrace}");
            throw;
        }
    }

    /// <summary>
    /// Replacement for NavDatabase.CollationAwareStringComparer getter.
    /// Returns a CollationAwareStringComparer using InvariantCulture + IgnoreCase.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object? NavDatabase_get_CollationAwareStringComparer(NavDatabase self)
    {
        // Check if already populated on the skeleton instance (set by EnsureSkeletonDatabase).
        if (_fNavDatabaseCollation != null)
        {
            var existing = _fNavDatabaseCollation.GetValue(self);
            if (existing != null) return existing;
        }
        var built = BuildCollationAwareComparer();
        if (built != null && _fNavDatabaseCollation != null)
            _fNavDatabaseCollation.SetValue(self, built);
        return built;
    }

    /// <summary>
    /// Replacement for NCLMetaApplicationObject.get_ApplicationObjectClrType.
    /// The real getter does lock(nclMetaObjectCLRTypeContainer) which NREs when the container
    /// is null (our CreateFromMetaTable-built tables never go through NCLCodeLoader).
    /// Instead, look up Record{ID} in the currently-loaded assemblies.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Type? NCLMetaApplicationObject_get_ApplicationObjectClrType(object self)
    {
        // objectId is declared on base NCLMetaApplicationObject. Non-public fields are not
        // discovered through inheritance by GetField — walk up the type chain manually.
        FieldInfo? objIdField = null;
        for (var t = self?.GetType(); t != null && objIdField == null; t = t.BaseType)
            objIdField = t.GetField("objectId", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        if (objIdField == null) return null;
        var objId = objIdField.GetValue(self);
        if (objId == null) return null;
        var numProp = objId.GetType().GetProperty("ObjectNumber",
            BindingFlags.Public | BindingFlags.Instance);
        if (numProp == null) return null;
        int id = (int)numProp.GetValue(objId)!;

        // Branch on ObjectType so this getter resolves correctly when the receiver
        // is an NCLMetaForm / NCLMetaReport (§P).  Tables are the §O default.
        var typeProp = objId.GetType().GetProperty("ObjectType",
            BindingFlags.Public | BindingFlags.Instance);
        var ot = typeProp?.GetValue(objId)?.ToString();
        return ot switch
        {
            "Page"     => FindClrTypeByName($"Form{id}"),
            "Report"   => FindClrTypeByName($"Report{id}"),
            "CodeUnit" => FindClrTypeByName($"Codeunit{id}"),
            _          => FindRecordType(id),
        };
    }

    // Metadata-backed lookup — see FindRecordTypeIn in RecordPatches.NclMetaTableBuilder.cs.
    private static Type? FindClrTypeByName(string name)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = AlRunner.Infrastructure.AssemblyTypeIndex.For(asm).FindFirst(name);
                if (t != null) return t;
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Replacement for SequentialUuidCreator.NativeMethods.NewSequentialId.
    /// The original P/Invokes rpcrt4.dll!UuidCreateSequential which doesn't exist on Linux.
    /// Replace with a standard Guid.NewGuid() on all platforms.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Guid NewSequentialId_Replacement()
        => Guid.NewGuid();

    // ------------------------------------------------------------------
    // NavTextConstant.get_Value — sync underbelly hit by every NavText(constant) ctor
    // ------------------------------------------------------------------
    //
    // The real getter chains through `NavCurrentThread.ResolveAppGroup().GroupId` and
    // `NavCurrentThread.Session.LocalLanguage / GlobalLanguage` to pick a language.
    // NavCurrentThread.Session is null on the skeleton thread → NRE on every read of a
    // NavTextConstant (which the AL emitter generates for every Label, including the
    // five `TestFieldValidationCodeTxt`/`TestFieldCodeTxt`/etc. used by Assert codeunit).
    //
    // Empirically this is what causes `Assert.ExpectedTestFieldError` to NRE in Release
    // mode: the AL-emit OnRun has expressions like
    //     `NavTextExtensions.ALContains(this.lastErrorCode, new NavText(testFieldValidationCodeTxt))`
    // and the `new NavText(constant)` invokes the implicit `NavStringValue → string`
    // conversion which calls `NavTextConstant.Value` which NREs. Debug-mode emit happens
    // to evaluate these in a different order that hides the NRE; Release-mode does not.
    //
    // Replace with a skeleton-safe lookup: pick the first English (LCID 1033) entry, or
    // the first non-default entry, or empty. AL ships single-language ENU labels in v2,
    // so the result is byte-identical to what the real getter returns under normal
    // session state (LocalLanguage = 1033, fallback = 1033).

    private static FieldInfo? _fNavTextConstant_multiLanguage;
    private static FieldInfo? _fMultiLanguage_languageIds;
    private static FieldInfo? _fMultiLanguage_texts;

    /// <summary>
    /// Replacement for NavStringValue.op_Implicit(NavStringValue → string). Original is
    /// `value?.Value` — but `Value` on NavTextConstant NREs through NavCurrentThread.Session.
    /// Route through our skeleton-safe NavTextConstant_get_Value when the input is a
    /// NavTextConstant; otherwise read Value normally (other subtypes don't NRE).
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, PropertyInfo?> _pValueByType = new();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string NavStringValue_op_Implicit(object? value)
    {
        if (value == null) return null!;
        var t = value.GetType();
        if (t.Name == "NavTextConstant")
            return NavTextConstant_get_Value(value);
        var prop = _pValueByType.GetOrAdd(t,
            x => x.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance));
        return prop?.GetValue(value) as string ?? string.Empty;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string NavTextConstant_get_Value(object self)
    {
        if (self == null) return string.Empty;
        try
        {
            if (_fNavTextConstant_multiLanguage == null)
                _fNavTextConstant_multiLanguage = self.GetType().GetField("multiLanguage",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            var ml = _fNavTextConstant_multiLanguage?.GetValue(self);
            if (ml == null) return string.Empty;
            if (_fMultiLanguage_languageIds == null)
                _fMultiLanguage_languageIds = ml.GetType().GetField("languageIds",
                    BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? ml.GetType().GetField("LanguageIds",
                        BindingFlags.NonPublic | BindingFlags.Instance);
            if (_fMultiLanguage_texts == null)
                _fMultiLanguage_texts = ml.GetType().GetField("texts",
                    BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? ml.GetType().GetField("Texts",
                        BindingFlags.NonPublic | BindingFlags.Instance);
            // Try LanguageIds/Texts as properties (the public API on MultiLanguage).
            var langProp = ml.GetType().GetProperty("LanguageIds",
                BindingFlags.Public | BindingFlags.Instance);
            var textProp = ml.GetType().GetProperty("Texts",
                BindingFlags.Public | BindingFlags.Instance);
            var langs = langProp?.GetValue(ml) ?? _fMultiLanguage_languageIds?.GetValue(ml);
            var texts = textProp?.GetValue(ml) ?? _fMultiLanguage_texts?.GetValue(ml);
            if (langs is System.Collections.IList ll && texts is System.Collections.IList tl && ll.Count > 0 && tl.Count > 0)
            {
                // Prefer English (1033) first, then any non-default.
                for (int i = 0; i < ll.Count && i < tl.Count; i++)
                {
                    if (ll[i] is int lcid && lcid == 1033 && tl[i] is string s && !string.IsNullOrEmpty(s))
                        return s;
                }
                if (tl[0] is string first) return first;
            }
        }
        catch { }
        return string.Empty;
    }

    // ------------------------------------------------------------------
    // NavRecord.TestFieldNotBlank / TestFieldEquals / TestFieldError
    // ------------------------------------------------------------------
    //
    // These three methods are the sync underbelly of `Rec.TestField(...)`.
    // The real implementations format their failure message using:
    //   - `base.Session.WindowsCulture`
    //   - `ALFieldCaptionAsync(...).AsTask().GetAwaiter().GetResult()`
    //   - `metaField.Parent.TableCaptionSafe`
    //   - `PrimaryKeyString` (iterates key fields)
    //   - `TryAddTestFieldAction(metaField)` (touches `Session.Diagnostics`,
    //      `Session.Permissions`, `NavGlobal.NCLMetadata.GetMetaFormById` —
    //      none of which the skeleton runtime initializes)
    //
    // Empirically (verified 2026-05-09 with Debug-mode emit) the throw path
    // raises a NullReferenceException somewhere inside that argument list,
    // which then surfaces with error code "NullReference" instead of
    // "TestField". Assert.ExpectedTestFieldError sees the wrong code, calls
    // its own Error path, and the test fails with NRE 12 times.
    //
    // Per HANDOFF §5.2 (Option C) we replace the throw path with a clean
    // `NavTestFieldException.CreateNonblank/CreateMustBeEqualTo` call using
    // safe arguments — same factory the real BC code uses, just with culture
    // forced to InvariantCulture and PrimaryKeyValues="" to avoid the
    // failing-skeleton property dives. The exception type is identical
    // (`NavTestFieldException`) so `GetErrorCode` returns "TestField" and
    // Assert.ExpectedTestFieldError's `LastErrorCode.Contains("TestField")`
    // matches. The message contains the field caption, satisfying
    // ExpectedTestFieldMessage's StrPos check.
    //
    // The pass path (value is non-blank / equal) is left to the real code:
    // we delegate to it via reflection and only intercept the throw path.

    private static MethodInfo? _mGetFieldValue;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, PropertyInfo?> _pIsZeroOrEmptyByType = new();
    private static MethodInfo? _mNavTestFieldException_CreateNonblank;
    private static MethodInfo? _mNavTestFieldException_CreateMustBeEqualTo;
    private static PropertyInfo? _pNCLMetaFieldFieldNo;
    private static PropertyInfo? _pNCLMetaFieldFieldName;
    private static PropertyInfo? _pNCLMetaFieldParent;
    private static PropertyInfo? _pNCLMetaTableTableName;

    private static string SafeFieldName(object? metaField)
    {
        if (metaField == null) return string.Empty;
        if (_pNCLMetaFieldFieldName == null)
            _pNCLMetaFieldFieldName = metaField.GetType().GetProperty("FieldName",
                BindingFlags.Public | BindingFlags.Instance);
        return (string?)_pNCLMetaFieldFieldName?.GetValue(metaField) ?? string.Empty;
    }

    private static string SafeTableName(object? metaField)
    {
        if (metaField == null) return string.Empty;
        if (_pNCLMetaFieldParent == null)
            _pNCLMetaFieldParent = metaField.GetType().GetProperty("Parent",
                BindingFlags.Public | BindingFlags.Instance);
        var parent = _pNCLMetaFieldParent?.GetValue(metaField);
        if (parent == null) return string.Empty;
        if (_pNCLMetaTableTableName == null)
            _pNCLMetaTableTableName = parent.GetType().GetProperty("TableName",
                BindingFlags.Public | BindingFlags.Instance);
        return (string?)_pNCLMetaTableTableName?.GetValue(parent) ?? string.Empty;
    }

    private static object CreateNavTestFieldException_Nonblank(object metaField)
    {
        if (_mNavTestFieldException_CreateNonblank == null)
        {
            var navTypes = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types");
            var t = navTypes?.GetType("Microsoft.Dynamics.Nav.Types.Exceptions.NavTestFieldException")
                ?? navTypes?.GetType("Microsoft.Dynamics.Nav.Types.NavTestFieldException")
                ?? navTypes?.GetTypes().FirstOrDefault(x => x.Name == "NavTestFieldException");
            _mNavTestFieldException_CreateNonblank = t?.GetMethod("CreateNonblank",
                BindingFlags.Public | BindingFlags.Static);
        }
        var m = _mNavTestFieldException_CreateNonblank
            ?? throw new InvalidOperationException("NavTestFieldException.CreateNonblank not found");
        // Signature: (CultureInfo, string fieldName, string tableName, string primaryKeyValues, ErrorInfoData=null)
        var args = new object?[] { System.Globalization.CultureInfo.InvariantCulture,
            SafeFieldName(metaField), SafeTableName(metaField), string.Empty, null };
        return (Exception)m.Invoke(null, args)!;
    }

    private static object CreateNavTestFieldException_MustBeEqualTo(object metaField, string shouldBe, string current)
    {
        if (_mNavTestFieldException_CreateMustBeEqualTo == null)
        {
            var navTypes = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types");
            var t = navTypes?.GetType("Microsoft.Dynamics.Nav.Types.Exceptions.NavTestFieldException")
                ?? navTypes?.GetType("Microsoft.Dynamics.Nav.Types.NavTestFieldException")
                ?? navTypes?.GetTypes().FirstOrDefault(x => x.Name == "NavTestFieldException");
            _mNavTestFieldException_CreateMustBeEqualTo = t?.GetMethod("CreateMustBeEqualTo",
                BindingFlags.Public | BindingFlags.Static);
        }
        var m = _mNavTestFieldException_CreateMustBeEqualTo
            ?? throw new InvalidOperationException("NavTestFieldException.CreateMustBeEqualTo not found");
        // Signature: (CultureInfo, string fieldName, string tableName, string shouldBeValue,
        //            string currentValue, string primaryKeyValues, ErrorInfoData=null)
        var args = new object?[] { System.Globalization.CultureInfo.InvariantCulture,
            SafeFieldName(metaField), SafeTableName(metaField), shouldBe, current, string.Empty, null };
        return (Exception)m.Invoke(null, args)!;
    }

    private static bool TryGetFieldValueIsZeroOrEmpty(object navRecord, object metaField, out bool result)
    {
        result = true;
        try
        {
            // Look up by the NCLMetaField base parameter type (the runtime metaField may be
            // a derived subclass that the public method-resolution can't match directly).
            if (_mGetFieldValue == null)
            {
                var nclMetaFieldT = metaField.GetType();
                while (nclMetaFieldT != null && nclMetaFieldT.Name != "NCLMetaField")
                    nclMetaFieldT = nclMetaFieldT.BaseType;
                _mGetFieldValue = navRecord.GetType().GetMethod("GetFieldValue",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { nclMetaFieldT ?? metaField.GetType() }, null);
            }
            var v = _mGetFieldValue?.Invoke(navRecord, new object[] { metaField });
            if (v == null) { result = true; return true; }
            // NavValue.IsZeroOrEmpty is virtual; cache the PropertyInfo per concrete subtype
            // because the NCLMetaField argument can be different NavValue subclasses across
            // calls (NavInteger / NavText / NavCode / …).
            var p = _pIsZeroOrEmptyByType.GetOrAdd(v.GetType(),
                t => t.GetProperty("IsZeroOrEmpty", BindingFlags.Public | BindingFlags.Instance));
            var b = p?.GetValue(v) as bool?;
            result = b ?? true;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Replacement for NavRecord.TestFieldNotBlank(NCLMetaField, NavALErrorInfo).
    /// Real method computes the error message via Session.WindowsCulture, async ALFieldCaption,
    /// PrimaryKeyString, and TryAddTestFieldAction — all of which dereference skeleton state
    /// that's null. Compute the not-blank predicate via the real `GetFieldValue/IsZeroOrEmpty`
    /// (those work on a populated record) and on the throw path raise a NavTestFieldException
    /// directly with InvariantCulture and minimal args, so error code is "TestField" and the
    /// message contains the field caption — what Assert.ExpectedTestFieldError expects.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavRecord_TestFieldNotBlank(object self, object metaField, object? errorInfo)
    {
        if (metaField == null) throw new ArgumentNullException(nameof(metaField));
        if (TryGetFieldValueIsZeroOrEmpty(self, metaField, out var isBlank) && !isBlank)
            return; // value is set — nothing to assert.
        throw (Exception)CreateNavTestFieldException_Nonblank(metaField);
    }

    /// <summary>
    /// Replacement for NavRecord.TestFieldError(NCLMetaField, string, NavALErrorInfo).
    /// Same rationale as TestFieldNotBlank — computes the error message safely.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavRecord_TestFieldError(object self, object metaField, string shouldBeValue, object? errorInfo)
    {
        if (metaField == null) throw new ArgumentNullException(nameof(metaField));
        string current = "<N/A>";
        try
        {
            if (_mGetFieldValue == null)
                _mGetFieldValue = self.GetType().GetMethod("GetFieldValue",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { metaField.GetType() }, null);
            var v = _mGetFieldValue?.Invoke(self, new object[] { metaField });
            current = v?.ToString() ?? "<N/A>";
        }
        catch { /* leave default */ }
        throw (Exception)CreateNavTestFieldException_MustBeEqualTo(metaField, shouldBeValue ?? string.Empty, current);
    }

}
