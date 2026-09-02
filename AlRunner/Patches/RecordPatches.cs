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
        _bcSymbolExtensionIndexBuilt = false; // re-merge BC precompiled extensions on next build
        _fieldTriggersWiredTables.Clear();
        _parsedPages.Clear();
        _parsedPageExtensions.Clear();
        _parsedReports.Clear();
        _parsedReportExtensions.Clear();
        _parsedQueries.Clear();
        _parsedXmlPorts.Clear();
        _parsedObjectDecls.Clear();
        _parsedObjectCaptions.Clear();
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
                foreach (var file in AlRunner.Infrastructure.SafeDirectoryScan.Files(dir, "*.al"))
                    ParseSourceFileIntoAllExtractors(File.ReadAllText(file), file);
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
    /// via the private Filter() helper and accumulates count/sum/average (the only three
    /// calculation methods routed through CalcNumeric — Exists and Lookup use separate paths).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object TempTableDataProvider_CalcNumeric(object self, object request)
    {
        var rt = request.GetType();
        var companyToken   = (int)rt.GetProperty("CompanyToken")!.GetValue(request)!;
        var filtersAndMarks = rt.GetProperty("FiltersAndMarks")!.GetValue(request);
        var sourceTable    = (NCLMetaTable)rt.GetProperty("MetaApplicationObject")!.GetValue(request)!;
        var fieldsToCalc   = (FieldList)rt.GetProperty("FieldsToCalculate")!.GetValue(request)!;
        int fieldCount     = fieldsToCalc.Count;

        var primaryKeySortingFields = _fTtdpPrimaryKeySortingFields!.GetValue(self);
        var sums = new Decimal18[fieldCount];
        int recordCount = 0;

        var rows = (System.Collections.IEnumerable)_mTtdpFilter!.Invoke(
            self, new object?[] { companyToken, filtersAndMarks, null, primaryKeySortingFields, false })!;

        foreach (TempTableRecordBuffer row in rows)
        {
            checked { recordCount++; }
            for (int i = 0; i < fieldCount; i++)
            {
                var field = (NCLMetaField)fieldsToCalc[i];
                var cm = field.CalculationFormula.CalculationMethod;
                if (cm is NCLMetaCalculationMethod.Sum or NCLMetaCalculationMethod.Average)
                {
                    var srcField = sourceTable.GetFieldByNo(field.CalculationFormula.FieldId, trapError: true);
                    if (srcField != null)
                    {
                        var v = row[srcField.ColumnIndex];
                        if (v != null) sums[i] = checked(sums[i] + v.ToDecimal());
                    }
                }
            }
        }

        var tuples = new Tuple<INavFieldMetadata, NavValue>[fieldCount];
        for (int j = 0; j < fieldCount; j++)
        {
            var field = (NCLMetaField)fieldsToCalc[j];
            NavValue navValue = field.CalculationFormula.CalculationMethod switch
            {
                NCLMetaCalculationMethod.Count   => NavValue.CreateNavValueFromObject(field, recordCount),
                NCLMetaCalculationMethod.Average when recordCount > 0
                                                 => NavValue.CreateNavValueFromObject(field, sums[j] / recordCount),
                _                                => NavValue.CreateNavValueFromObject(field, sums[j]),
            };
            tuples[j] = new Tuple<INavFieldMetadata, NavValue>(field, navValue);
        }

        return _ctorFieldDictionaryNavValue!.Invoke(new object[] { tuples })!;
    }

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
        var rec = (NavRecord)ctor.Invoke(new object?[] { self, metaTable, isTemp, null, null,
            SecurityFiltering.Validated });
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
        return rec;
    }

    /// <summary>
    /// Replacement for NavSession.DataAccessSource getter.
    /// Returns a skeleton DataAccessSource backed by TempTableDataProvider (in-memory).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object? NavSession_get_DataAccessSource(NavSession self)
    {
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
    }

    /// <summary>
    /// The --test-data on-demand load, installed by TestDataProvisioner.Arm() and null for
    /// every run that did not pass the flag (so a default run pays one null check per
    /// first-touch of a table). A delegate rather than a direct call because this file is the
    /// MECHANISM half: which tables a backup offers, and from which backup, is policy, and it
    /// lives in TestDataProvisioner. Arguments are (DataAccessSource, tableId).
    /// </summary>
    internal static Action<object, int>? TestDataOnDemandLoader;

    /// <summary>Re-entrancy depth for the loader — NOT a "which tables are loaded" cache,
    /// which #2262 rules out on purpose.
    ///
    /// Hydrating a table runs BC's own metadata and NavValue construction, and that code can
    /// reach a Record of ANOTHER table, which lands back in GetDataAccessForTableCore and
    /// would recurse. Skipping the nested load is safe rather than lossy: the nested table is
    /// left with empty storage, which is the same state it would have had a moment earlier,
    /// so the next independent touch of it loads it normally.</summary>
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
        if (!isTemporary)
            BlobStoreIsolationPatches.MarkDatabaseBacked(dataAccess);

        return dataAccess;
    }

    private static object GetDataAccessForTableCore(object self, NCLMetaTable table, bool isTemporary)
    {
        try
        {
            if (isTemporary)
                return _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;

            // Per-(DataAccessSource, tableId) cache so Insert+Find on the same regular table
            // share storage.
            var perTable = _dataAccessByTable.GetValue(self,
                static _ => new ConcurrentDictionary<int, object>());
            var tableId = table.TableId;

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
                    ?? throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                        "Field (virtual table 2000000041)",
                        "field-virtual-table — DataAccessSource has no skeleton session; see docs/scope.md");
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

            if (perTable.TryGetValue(tableId, out var cached))
            {
                return cached;
            }
            var result = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
            // Race: keep first winner.
            var added = perTable.GetOrAdd(tableId, result);
            // ── --test-data on-demand load (#2262) ───────────────────────────────────────
            // Reaching here means the store did NOT have this table, which is exactly when a
            // --test-data run needs its rows read out of the backup. Same choke point the
            // virtual tables above use, and for the same reason: it is the only place a
            // table's storage is materialised, so the load always lands before the operation
            // that triggered it — a read and a write are equally covered.
            //
            // Storage presence IS the "have we loaded this" answer, so there is no flag to
            // keep in step: RestoreInstallBaselineSnapshot repopulates perTable from exactly
            // the snapshot it restores, so a table the last restore carried is present and
            // does not reach this line. See TestDataProvisioner's header.
            //
            // Only the GetOrAdd winner loads. A loser would be hydrating into storage it is
            // about to throw away, and the winner's rows are already in the returned instance.
            if (TestDataOnDemandLoader != null && ReferenceEquals(added, result))
                InvokeTestDataOnDemandLoader(self, tableId);
            return added;
        }
        catch (Exception ex)
        {
            var inner = ex is System.Reflection.TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            Console.Error.WriteLine($"[RecordPatches] GetDataAccessForTable failed for table {table?.TableName ?? "null"}: {inner.GetType().Name}: {inner.Message}");
            Console.Error.WriteLine(inner.StackTrace ?? "");
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
