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
using AlRunnerV2.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunnerV2.Patches;

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
    private static Type? _tMetaCalcFormula;
    private static Type? _tMetaFilter;
    private static Type? _tFilterType;
    private static Type? _tNCLMetaTable;
    private static MethodInfo? _mCreateFromMetaTable;
    private static MethodInfo? _mCreateForTempTable;
    private static FieldInfo? _fVatdcInstance;
    private static Type? _tDataAccessSource;
    private static MethodInfo? _mCreateTempDataAccess;
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

    // NCLMetaTable.GetFieldByNo(int extensionObjectId, int fieldNo) — hooked to fall back
    // to TryGetFieldByNo when the extension object isn't registered (unresolved extension).
    private static MethodInfo? _mGetFieldByNoExt;

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
        _parsedReports.Clear();
        _parsedReportExtensions.Clear();
        _parsedQueries.Clear();
        _parsedXmlPorts.Clear();
        _metaFormCache.Clear();
        _metaReportCache.Clear();
        _metaQueryCache.Clear();
        _metaXmlPortCache.Clear();
        _sourceDirs.Clear();
        // Drop the in-memory table rows so an edited re-run starts clean instead of
        // seeing Inserts from the previous run (which would e.g. throw "already exists").
        _dataAccessByTable.Clear();
    }

    public static void AddSourceDir(string dir)
    {
        if (!Directory.Exists(dir)) return;
        _sourceDirs.Add(dir);
        // If Register() already ran (it runs before the bucket loop), parse immediately
        // and feed the freshly-parsed tables into the NCLMetadata cache.
        if (_registered)
        {
            foreach (var file in Directory.GetFiles(dir, "*.al", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);
                TryParseTableFile(text);
                TryParseTableExtensionFile(text);
                TryParsePageFile(text);
                TryParseReportFile(text);
                TryParseQueryFile(text);
                TryParseXmlPortFile(text);
            }
            PopulateNclMetadataCache();
        }
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
        _tMetaCalcFormula = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaCalcFormula")!;
        _tMetaFilter  = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaFilter")!;
        _tFilterType  = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.FilterType")!;

        // NCLMetaTable and factory (Microsoft.Dynamics.Nav.Runtime / Ncl)
        _tNCLMetaTable = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaTable")!;
        _mCreateFromMetaTable = _tNCLMetaTable.GetMethod("CreateFromMetaTable",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var getMetaTableById = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetadata")?
            .GetMethod("GetMetaTableById", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(int), typeof(bool), typeof(int) }, null);
        if (getMetaTableById != null)
        {
            var repl = typeof(RecordPatches).GetMethod(nameof(NCLMetadata_GetMetaTableById),
                BindingFlags.Public | BindingFlags.Static)!;
            JmpHook.Apply(getMetaTableById, repl, "NCLMetadata.GetMetaTableById(int,bool,int)");
        }
        var objectTypeType = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.ObjectType")!;
        var applicationObjectIdType = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.ApplicationObjectId")!;
        var getMetaApplicationObjectByType = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetadata")?
            .GetMethod("GetMetaApplicationObject", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { objectTypeType, typeof(int), typeof(bool), typeof(int) }, null);
        if (getMetaApplicationObjectByType != null)
        {
            var repl = typeof(RecordPatches).GetMethod(nameof(NCLMetadata_GetMetaApplicationObjectByType),
                BindingFlags.Public | BindingFlags.Static)!;
            JmpHook.Apply(getMetaApplicationObjectByType, repl, "NCLMetadata.GetMetaApplicationObject(ObjectType,int,bool,int)");
        }
        var getMetaApplicationObjectById = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetadata")?
            .GetMethod("GetMetaApplicationObject", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { applicationObjectIdType, typeof(bool), typeof(int) }, null);
        if (getMetaApplicationObjectById != null)
        {
            var repl = typeof(RecordPatches).GetMethod(nameof(NCLMetadata_GetMetaApplicationObjectById),
                BindingFlags.Public | BindingFlags.Static)!;
            JmpHook.Apply(getMetaApplicationObjectById, repl, "NCLMetadata.GetMetaApplicationObject(ApplicationObjectId,bool,int)");
        }

        // Hook NCLMetaTable.GetFieldByNo(int extensionObjectId, int fieldNo) to fall back
        // to TryGetFieldByNo when the extension object isn't registered in our skeleton.
        _mGetFieldByNoExt = _tNCLMetaTable.GetMethod("GetFieldByNo",
            BindingFlags.Public | BindingFlags.Instance, null,
            new[] { typeof(int), typeof(int) }, null);
        if (_mGetFieldByNoExt != null)
        {
            var repl = typeof(RecordPatches).GetMethod(nameof(NCLMetaTable_GetFieldByNoExt),
                BindingFlags.Public | BindingFlags.Static)!;
            JmpHook.Apply(_mGetFieldByNoExt, repl, "NCLMetaTable.GetFieldByNo(extensionId,fieldNo)");
        }

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

        // Parse AL source files (tables + pages + reports — same set of dirs).
        ParseAllSources();
        ParseAllPageSources();
        ParseAllReportSources();
        ParseAllQuerySources();
        ParseAllXmlPortSources();

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
        Console.Error.WriteLine($"[RecordPatches] NavSession_get_Database: _skeletonDatabase={_skeletonDatabase?.GetType().Name ?? "null"}");
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

        // Build skeleton factory (uninitialized — the ctor requires a real TreeObject).
        var factory = RuntimeHelpers.GetUninitializedObject(tFactory);

        // Build skeleton GlobalTriggers (uninitialized).
        // NavSystemCodeunitGlobalTriggers.GetTriggersOnTable checks session.IsCompanyOpen first.
        // Now that we seed hasBeenOpened+companyName (so IsCompanyOpen=true and the real
        // ALCompanyName/ALCurrentCompany paths work), GetTriggersOnTable proceeds past the
        // short-circuit and tries `Monitor.TryEnter(triggersOnTables)`. The dict is normally
        // initialized via field initializer, but GetUninitializedObject skips that — so we
        // must populate it here, otherwise Monitor.TryEnter NREs ("Value cannot be null").
        var globalTriggers = RuntimeHelpers.GetUninitializedObject(tGlobalTriggers);
        var fSession = tGlobalTriggers.GetField("session",
            BindingFlags.NonPublic | BindingFlags.Instance);
        fSession?.SetValue(globalTriggers, skeletonSession);

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

        // Hook GetTriggersOnTable to short-circuit to Triggers.None for the headless runner.
        // The real body calls NavSystemCodeunit.Invoke(GetGlobalTableTriggerMask, …) on the
        // skeleton factory, whose own scope-machinery state is uninitialized → NRE inside
        // InvokeAsync. The headless runner has no AL global-trigger codeunits, so Triggers.None
        // is observably equivalent to BC running with no [EventSubscriber(GlobalTriggers,…)] hooked.
        // Faithful per docs/scope.md §2.
        var mGetTriggersOnTable = tGlobalTriggers.GetMethod("GetTriggersOnTable",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (mGetTriggersOnTable != null)
        {
            var repl = typeof(BcRuntime).GetMethod(nameof(BcRuntime.NavSystemCodeunitGlobalTriggers_GetTriggersOnTable),
                BindingFlags.Public | BindingFlags.Static);
            if (repl != null)
                JmpHook.Apply(mGetTriggersOnTable, repl, "NavSystemCodeunitGlobalTriggers.GetTriggersOnTable");
        }

        // Wire global triggers into factory.
        var fGlobalTriggers = tFactory.GetField("globalTriggers",
            BindingFlags.NonPublic | BindingFlags.Instance);
        fGlobalTriggers?.SetValue(factory, globalTriggers);

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
            var trackChanges = RuntimeHelpers.GetUninitializedObject(tTrackChanges);
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
                AlRunnerV2.Infrastructure.RunnerScope.ThrowNotYetImplemented(
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
        var rec = (NavRecord)ctor.Invoke(new object?[] { self, metaTable, isTemp, null, null,
            SecurityFiltering.Ignored });
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
        AlRunnerV2.Patches.EventSubscriberPatches.InjectValidateSubsForTable(id, metaTable);
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
        AlRunnerV2.Patches.RecordLinkPatches.ResetForTest();

        // IsolatedStorage in-memory store — per-test reset matches BC semantics where
        // a test's writes are rolled back on completion.
        AlRunnerV2.Patches.TenantStoragePatches.ResetForTest();
    }

    public static object NavDataAccessSource_GetDataAccessForTable(object self, NCLMetaTable table, bool isTemporary)
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
            if (perTable.TryGetValue(tableId, out var cached))
            {
                return cached;
            }
            var result = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
            // Race: keep first winner.
            var added = perTable.GetOrAdd(tableId, result);
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

    private static Type? FindClrTypeByName(string name)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = Array.Find(asm.GetTypes(), x => x.Name == name);
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

    // NCLMetaField.get_FieldCaption — original computes via captionStrings →
    // NavCurrentThread.ResolveAppGroup(Session) → MetaField.GetMergedCaptionMultiLanguage,
    // which dereferences LanguageProvider.Provider / NavCurrentThread.Session state that
    // the skeleton runtime hasn't populated. Per HANDOFF §5.2 this is the sync underbelly
    // the async ALFieldCaptionAsync wrapper falls through to — populating a stable value
    // here lights up Rec.TestField error formatting (~40+ tests) without touching the
    // async surface. AL doesn't ship per-language captions to v2 anyway, so FieldName
    // is the same string the real getter produces under FieldIsNotFromMetadata.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string NCLMetaField_get_FieldCaption(object self)
    {
        if (self == null) return string.Empty;
        var fieldNameProp = self.GetType().GetProperty("FieldName",
            BindingFlags.Public | BindingFlags.Instance);
        return (string?)fieldNameProp?.GetValue(self) ?? string.Empty;
    }
}
