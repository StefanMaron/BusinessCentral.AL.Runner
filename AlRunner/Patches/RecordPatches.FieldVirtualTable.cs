// RecordPatches.FieldVirtualTable — managed provider for the virtual Field system
// table (2000000041).
//
// WHY THIS EXISTS
//   BC's "Library - Workflow".EnableWorkflow (and any AL that enumerates the Field
//   table) does `Field.SetRange(TableNo, <t>); Field.FindSet()` and throws
//   "There is no Field within the filter." when zero rows come back. On the real
//   service tier the Field table is a VIRTUAL table: its rows are computed on the
//   fly by Microsoft.Dynamics.Nav.Runtime.FieldDataProvider from NCLMetadata —
//   one row per NCLMetaField of the filtered TableNo. There are no stored rows.
//
//   Our runtime routes EVERY table's data access through
//   NavDataAccessSource_GetDataAccessForTable → an in-memory TempTableDataProvider.
//   For 2000000041 that store is empty, so the iteration yields nothing → the throw.
//
//   Routing 2000000041 to BC's OWN GetVirtualDataAccess → native
//   FieldDataProvider.FindAsync SIGSEGVs (exit 139) in BC's R2R-precompiled async
//   find state machine on the skeleton session (same native-find wall as the
//   query-join engine). So we cannot use the native find path.
//
// WHAT THIS DOES (faithful, managed, R2R-safe)
//   When the hook is asked for the data access of table 2000000041 we still build
//   our in-memory TempTableDataProvider (so BC's own filter/sort/Find engine runs
//   over it and applies the AL filters — TableNo, No.<>1, Type<>BLOB,
//   ObsoleteState<>Removed — exactly as the service tier would). We then POPULATE
//   that store with REAL Field rows built by BC's OWN row-builder
//   FieldDataProvider.GetFieldRecordBuffer(...) — one ReadOnlyRecordBuffer per
//   NCLMetaField of every source table currently materialised in the skeleton
//   metadata cache. GetFieldRecordBuffer is pure managed code that reads
//   NCLMetaField properties and fills the Field table's NavValue[] at BC's exact
//   ordinals; it does NOT touch the crashing native FindAsync path.
//
//   Because the AL TableNo filter is not known at data-access-creation time (it is
//   set on the AL record AFTER the data access is acquired), we populate rows for
//   every table present in the metadata cache, and TOP UP on every subsequent
//   access for any table that has since been built. BC's filter engine then narrows
//   to the requested TableNo at Find time. This is general — any TableNo the AL code
//   filters Field by — not hardcoded to any one table.
//
// PRECOMPILED-DLL RESPECT
//   We touch no BC business-logic body. FieldDataProvider, NCLMetaTable, NavValue,
//   ReadOnlyRecordBuffer, TempTableDataProvider are runtime-engine types; we invoke
//   BC's own GetFieldRecordBuffer by reflection and feed the result into our own
//   in-memory store. The rows are REAL field metadata (never fabricated), so the
//   loud-failures contract holds: no silent empties, no fake rows.
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunnerV2.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunnerV2.Patches;

public static partial class RecordPatches
{
    internal const int FieldVirtualTableId = 2000000041;

    // Reflection handles for the managed Field-row build + insert path.
    private static bool _fvtReflectionReady;
    private static Type? _tFieldDataProvider;
    private static object? _fieldDataProvider;          // single managed FieldDataProvider instance
    private static MethodInfo? _mGetFieldRecordBuffer;  // FieldDataProvider.GetFieldRecordBuffer(NavInteger,NavText,NCLMetaField,(NavGuid,NavGuid))
    private static FieldInfo? _fVdpMetaTable;           // VirtualDataProvider.metaTable (so we bind it to OUR 2000000041 metatable)
    private static MethodInfo? _mNavIntegerCreate;      // NavInteger.Create(int)
    private static MethodInfo? _mNavTextCreateTrunc;    // NavText.CreateTruncated(int,string)
    private static object? _navGuidDefault;             // NavGuid.Default
    private static Type? _tValueTupleGuid;              // (NavGuid, NavGuid)
    private static PropertyInfo? _pNclMetaTableAllFields;
    private static FieldInfo? _fNclMetaTableAllFields;
    private static MethodInfo? _mTtdpInsert;            // TempTableDataProvider.Insert(int, MutableRecordBuffer, InsertOptions, out ReadOnlyRecordBuffer)
    private static ConstructorInfo? _ctorMutableFromReadOnly; // MutableRecordBuffer(ReadOnlyRecordBuffer)
    private static object? _insertOptionsNone;

    // Per-(temp-provider) set of source TableNos already populated into that store,
    // so repeated accesses only top up newly-built tables (idempotent, no dup rows).
    private static readonly ConditionalWeakTable<object, ConcurrentDictionary<int, byte>> _fvtPopulatedByProvider = new();

    /// <summary>
    /// True if <paramref name="table"/> is the virtual Field system table (2000000041).
    /// </summary>
    private static bool IsFieldVirtualTable(NCLMetaTable? table)
        => table != null && table.TableId == FieldVirtualTableId;

    /// <summary>
    /// Populate the in-memory store behind the Field-table (2000000041) data access with
    /// REAL Field rows for every source table currently in the metadata cache. Idempotent
    /// per (provider, sourceTableNo). Called every time the 2000000041 data access is handed
    /// out so tables built after the first access also get their fields enumerated.
    /// </summary>
    private static void PopulateFieldVirtualTable(object dataAccess, NCLMetaTable fieldMetaTable, object session)
    {
        // Lazily seed NavGlobal.MetadataProvider the first time the Field table is accessed (so
        // non-Field tests keep baseline NavGlobal state). Required for FieldDataProvider's ctor.
        AlRunnerV2.BcRuntime.EnsureMetadataProviderSeeded();
        EnsureFieldVirtualTableReflection(fieldMetaTable, session);
        if (_mGetFieldRecordBuffer == null || _mTtdpInsert == null || _ctorMutableFromReadOnly == null)
            throw new AlRunnerV2.Infrastructure.RunnerOutOfScopeException(
                "Field (virtual table 2000000041)",
                "field-virtual-table — managed FieldDataProvider row-builder could not be bound; see docs/scope.md");

        EnsureDataAccessProviderReflection(dataAccess);
        var provider = _pDataAccessDataProvider!.GetValue(dataAccess)
            ?? throw new AlRunnerV2.Infrastructure.RunnerOutOfScopeException(
                "Field (virtual table 2000000041)",
                "field-virtual-table — Field data access has no in-memory provider; see docs/scope.md");

        // Make our 2000000041 metatable report IsVirtualTable=false so BC's RecordImplementation
        // find takes the NORMAL (temp-table) DataAccess path. IsVirtualTable for table 2000000041
        // reads (tableTypes & TableTypes.Virtual); we clear ONLY the Virtual bit (0x8), preserving
        // System/App/Tenant semantics. The field read is identical whether the getter is JIT'd or
        // R2R-inlined, so this affects native callers too.
        // NOTE: this is NECESSARY but NOT SUFFICIENT. Even with IsVirtualTable=false the subsequent
        // Field.FindSet() still SIGSEGVs in BC's R2R DataAccess.FindAsync async state machine BEFORE
        // it reaches our provider.Find (file-traced: our TempTableDataProvider.Find is hit 0 times).
        // See the gate rationale in RecordPatches.NavDataAccessSource_GetDataAccessForTable.
        ClearVirtualBit(fieldMetaTable);

        var done = _fvtPopulatedByProvider.GetValue(provider, static _ => new ConcurrentDictionary<int, byte>());

        // Snapshot the source tables currently materialised. Skip the Field table itself
        // and any other virtual/system table (they have no stored field rows of interest
        // here and FieldDataProvider builds Field rows for *real* tables).
        var sourceTableIds = _metaTableCache.Keys.Where(id => id != FieldVirtualTableId).ToArray();

        foreach (var srcId in sourceTableIds)
        {
            if (!done.TryAdd(srcId, 0))
                continue; // already populated this provider with srcId's fields

            NCLMetaTable? srcMeta;
            try { srcMeta = EnsureTableInMetadataCache(srcId); }
            catch { srcMeta = null; }
            if (srcMeta == null) continue;

            InsertFieldRowsForTable(provider, fieldMetaTable, srcMeta);
        }
    }

    /// <summary>
    /// Build one Field row per NCLMetaField of <paramref name="srcMeta"/> using BC's own
    /// FieldDataProvider.GetFieldRecordBuffer, and Insert each into the in-memory provider.
    /// </summary>
    private static void InsertFieldRowsForTable(object provider, NCLMetaTable fieldMetaTable, NCLMetaTable srcMeta)
    {
        var allFields = GetAllFields(srcMeta);
        if (allFields == null) return;

        var navTableNo = _mNavIntegerCreate!.Invoke(null, new object[] { srcMeta.TableId })!;
        // NCLMetaTable.TableName is a compile-time-known property on the statically referenced
        // Ncl type — read it directly. It used to go through a reflection handle bound only by
        // EnsureFieldVirtualTableReflection, where a failed bind degraded silently to "" (every
        // Field row would have reported an empty TableName rather than failing). No handle, no
        // ordering dependency, no silent default.
        var tableName = srcMeta.TableName ?? string.Empty;
        // tableNameField defined length is 30 in BC; CreateTruncated(30, name) matches the
        // service tier (it truncates to the Field table's "TableName" column length).
        var navTableName = _mNavTextCreateTrunc!.Invoke(null, new object[] { 30, tableName })!;
        var ids = BuildDefaultAppIds();
        foreach (var field in allFields)
        {
            if (field == null) continue;
            object readOnlyBuf;
            try
            {
                readOnlyBuf = _mGetFieldRecordBuffer!.Invoke(
                    _fieldDataProvider, new object?[] { navTableNo, navTableName, field, ids })!;
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                // A single field that cannot be projected must not silently vanish nor
                // poison the whole table — surface loudly so the gap is visible.
                throw new AlRunnerV2.Infrastructure.RunnerOutOfScopeException(
                    "Field (virtual table 2000000041)",
                    $"field-virtual-table — GetFieldRecordBuffer threw for table {srcMeta.TableId} field: " +
                    $"{tie.InnerException.GetType().Name}: {tie.InnerException.Message}; see docs/scope.md");
            }

            var mutable = _ctorMutableFromReadOnly!.Invoke(new object?[] { readOnlyBuf })!;
            var insertArgs = new object?[] { 0, mutable, _insertOptionsNone, null };
            try
            {
                _mTtdpInsert!.Invoke(provider, insertArgs);
            }
            catch (TargetInvocationException tie) when (
                tie.InnerException?.GetType().Name == "NavRecordAlreadyExistsException")
            {
                // Same (TableNo, FieldNo) already present (re-entrant top-up race). Faithful
                // to a virtual table where each (TableNo, FieldNo) is unique — skip the dup.
            }
        }
    }

    /// <summary>
    /// Bind the NCLMetaTable.AllFields accessors off the instance's OWN type.
    ///
    /// These handles used to be bound exclusively by EnsureFieldVirtualTableReflection,
    /// i.e. only once the Field virtual table (2000000041) had been touched. Any other
    /// caller of <see cref="GetAllFields"/> that ran FIRST therefore saw two null handles
    /// and got a null result that was indistinguishable from "this metatable genuinely has
    /// no fields" — which is exactly how the AllObj populate path came to report
    /// 'AllObj metatable has no field 1' during a dependency install trigger. Binding here,
    /// lazily and from the instance, removes the ordering dependency entirely.
    /// </summary>
    private static void EnsureNclMetaTableAllFieldsReflection(NCLMetaTable meta)
    {
        if (_pNclMetaTableAllFields != null || _fNclMetaTableAllFields != null) return;
        var tNclMetaTable = meta.GetType();
        _pNclMetaTableAllFields = tNclMetaTable.GetProperty("AllFields",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        _fNclMetaTableAllFields = tNclMetaTable.GetField("<AllFields>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (_pNclMetaTableAllFields == null && _fNclMetaTableAllFields == null)
            throw new InvalidOperationException(
                $"NCLMetaTable.AllFields not found on {tNclMetaTable.FullName} — BC metadata shape changed");
    }

    private static IEnumerable<NCLMetaField>? GetAllFields(NCLMetaTable meta)
    {
        EnsureNclMetaTableAllFieldsReflection(meta);
        var arr = (_pNclMetaTableAllFields?.GetValue(meta) ?? _fNclMetaTableAllFields?.GetValue(meta)) as Array;
        if (arr == null) return null;
        var list = new List<NCLMetaField>(arr.Length);
        foreach (var o in arr)
            if (o is NCLMetaField f) list.Add(f);
        return list;
    }

    private static object BuildDefaultAppIds()
    {
        // (NavGuid.Default, NavGuid.Default) — appPackageId / appRuntimePackageId. The Field
        // table's App Package Id / App Runtime Package Id columns are not part of the
        // EnableWorkflow filter set; a default guid is faithful for the skeleton (no app
        // package telemetry). GetFieldRecordBuffer only stores these into ordinals 60/61.
        return Activator.CreateInstance(_tValueTupleGuid!, _navGuidDefault, _navGuidDefault)!;
    }

    private const int TableTypesVirtualBit = 0x8;
    private static FieldInfo? _fNclMetaTableTableTypes;
    private static readonly HashSet<int> _virtualBitCleared = new();

    private static void ClearVirtualBit(NCLMetaTable fieldMetaTable)
    {
        if (_virtualBitCleared.Contains(FieldVirtualTableId)) return;
        _fNclMetaTableTableTypes ??= fieldMetaTable.GetType()
            .GetField("tableTypes", BindingFlags.NonPublic | BindingFlags.Instance);
        if (_fNclMetaTableTableTypes == null) return;

        var cur = _fNclMetaTableTableTypes.GetValue(fieldMetaTable);
        if (cur == null) return;
        // tableTypes is the [Flags] enum NCLMetaTable.TableTypes (underlying int).
        var curInt = Convert.ToInt32(cur);
        if ((curInt & TableTypesVirtualBit) == 0) { _virtualBitCleared.Add(FieldVirtualTableId); return; }
        var cleared = Enum.ToObject(_fNclMetaTableTableTypes.FieldType, curInt & ~TableTypesVirtualBit);
        FieldPoke.SetInstance(_fNclMetaTableTableTypes, fieldMetaTable, cleared);
        _virtualBitCleared.Add(FieldVirtualTableId);
    }

    private static void EnsureDataAccessProviderReflection(object dataAccess)
    {
        if (_pDataAccessDataProvider != null) return;
        var nclAsm = dataAccess.GetType().Assembly;
        var tDataAccess = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.DataAccess")!;
        _pDataAccessDataProvider = tDataAccess.GetProperty("DataProvider",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("DataAccess.DataProvider not found");
    }

    private static void EnsureFieldVirtualTableReflection(NCLMetaTable fieldMetaTable, object session)
    {
        if (_fvtReflectionReady) return;

        var nclAsm = fieldMetaTable.GetType().Assembly;
        const string rt = "Microsoft.Dynamics.Nav.Runtime.";

        _tFieldDataProvider = nclAsm.GetType(rt + "FieldDataProvider")
            ?? throw new InvalidOperationException("FieldDataProvider type not found");

        _mGetFieldRecordBuffer = _tFieldDataProvider.GetMethod("GetFieldRecordBuffer",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("FieldDataProvider.GetFieldRecordBuffer not found");

        var tVdp = nclAsm.GetType(rt + "VirtualDataProvider");
        _fVdpMetaTable = tVdp?.GetField("metaTable", BindingFlags.NonPublic | BindingFlags.Instance);

        // NavInteger.Create(int) / NavText.CreateTruncated(int,string) — in the Types asm.
        var navIntType = ResolveType(rt + "NavInteger", "Microsoft.Dynamics.Nav.Types.NavInteger");
        _mNavIntegerCreate = navIntType?.GetMethod("Create", BindingFlags.Public | BindingFlags.Static,
            binder: null, types: new[] { typeof(int) }, modifiers: null)
            ?? throw new InvalidOperationException("NavInteger.Create(int) not found");

        var navTextType = ResolveType(rt + "NavText", "Microsoft.Dynamics.Nav.Types.NavText");
        _mNavTextCreateTrunc = navTextType?.GetMethod("CreateTruncated", BindingFlags.Public | BindingFlags.Static,
            binder: null, types: new[] { typeof(int), typeof(string) }, modifiers: null)
            ?? throw new InvalidOperationException("NavText.CreateTruncated(int,string) not found");

        var navGuidType = ResolveType(rt + "NavGuid", "Microsoft.Dynamics.Nav.Types.NavGuid")
            ?? throw new InvalidOperationException("NavGuid type not found");
        _navGuidDefault = navGuidType.GetProperty("Default", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? navGuidType.GetField("Default", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? throw new InvalidOperationException("NavGuid.Default not found");
        _tValueTupleGuid = typeof(ValueTuple<,>).MakeGenericType(navGuidType, navGuidType);

        var tNclMetaTable = nclAsm.GetType(rt + "NCLMetaTable")!;
        _pNclMetaTableAllFields = tNclMetaTable.GetProperty("AllFields",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        _fNclMetaTableAllFields = tNclMetaTable.GetField("<AllFields>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance);

        var tTtdp = nclAsm.GetType(rt + "TempTableDataProvider")!;
        _mTtdpInsert = tTtdp.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "Insert" && m.GetParameters().Length == 4
                && m.GetParameters()[0].ParameterType == typeof(int))
            ?? throw new InvalidOperationException("TempTableDataProvider.Insert(int,MutableRecordBuffer,InsertOptions,out) not found");

        var tMutable = nclAsm.GetType(rt + "MutableRecordBuffer")!;
        var tReadOnly = nclAsm.GetType(rt + "ReadOnlyRecordBuffer")!;
        _ctorMutableFromReadOnly = tMutable.GetConstructor(new[] { tReadOnly })
            ?? throw new InvalidOperationException("MutableRecordBuffer(ReadOnlyRecordBuffer) ctor not found");

        var tInsertOptions = nclAsm.GetType(rt + "InsertOptions")!;
        _insertOptionsNone = Enum.ToObject(tInsertOptions, 0);

        // Build the single managed FieldDataProvider instance, bound to OUR 2000000041
        // metatable (the same instance handed to the temp store) so the NavValue[] layouts
        // match. FieldDataProvider..ctor(NavSession) reads NavGlobal.NCLMetadata /
        // NavGlobal.MetadataProvider (seeded by MetadataPatches) and resolves base.MetaTable
        // via NavGlobal.NCLMetadata.GetMetaTableById(2000000041) → our managed cache.
        _fieldDataProvider = BuildManagedFieldDataProvider(nclAsm, fieldMetaTable, session);

        _fvtReflectionReady = _fieldDataProvider != null;
    }

    private static object? BuildManagedFieldDataProvider(Assembly nclAsm, NCLMetaTable fieldMetaTable, object session)
    {
        const string rt = "Microsoft.Dynamics.Nav.Runtime.";
        var tFdp = _tFieldDataProvider!;

        // Prefer the real ctor (NavSession) — it caches all field-by-no handles the
        // row-builder needs (fieldNameField, classFieldField, …). If it throws (missing
        // NavGlobal state), surface loudly rather than fabricate.
        var ctor = tFdp.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, types: new[] { session.GetType() }, modifiers: null)
            ?? tFdp.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(c => c.GetParameters().Length == 1
                    && c.GetParameters()[0].ParameterType.Name == "NavSession");

        if (ctor == null)
            throw new InvalidOperationException("FieldDataProvider(NavSession) ctor not found");

        object fdp;
        try
        {
            fdp = ctor.Invoke(new object[] { session });
        }
        catch (TargetInvocationException tie)
        {
            var inner = tie.InnerException ?? tie;
            throw new AlRunnerV2.Infrastructure.RunnerOutOfScopeException(
                "Field (virtual table 2000000041)",
                $"field-virtual-table — FieldDataProvider ctor failed ({inner.GetType().Name}: {inner.Message}); " +
                "the skeleton NavGlobal.MetadataProvider/NCLMetadata is not seeded; see docs/scope.md");
        }

        // Bind base.metaTable to OUR 2000000041 instance so emitted rows align with the
        // temp store's table (belt-and-braces; the ctor's lazy getter resolves the same
        // cached instance anyway).
        if (_fVdpMetaTable != null)
            FieldPoke.SetInstance(_fVdpMetaTable, fdp, fieldMetaTable);

        return fdp;
    }

    private static Type? ResolveType(string runtimeName, string typesName)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = a.GetType(runtimeName) ?? a.GetType(typesName);
            if (t != null) return t;
        }
        return null;
    }
}
