// RecordPatches.QueryJoin — thin shim that delegates multi-dataitem query JOIN execution
// to the ISOLATED AlRunner.QueryJoin assembly (loaded lazily on first use).
//
// WHY THE EXECUTOR MOVED OUT OF al-runner.dll (the R2R load-chain contract):
//   The full join executor reflects over many Microsoft.Dynamics.Nav.Runtime types
//   (NCLMetaQueryDefinition, ReadOnlyRecordBuffer, NavValue, …). When that code was
//   compiled INTO al-runner.dll, the cumulative Ncl-type-referencing IL tipped the
//   process-startup ReadyToRun cross-assembly bind (al-runner.dll → Ncl.dll) over a
//   fragility cliff: even a minimal one-table Insert bundle SIGSEGV'd (exit 139) with
//   R2R ON, purely because the IL was present — never executed. (Sister class to
//   feedback_r2r_cecil_token_shift, on the al-runner→Ncl R2R-binding side.) Disabling
//   R2R made it pass, but R2R-off is not shippable.
//
//   Fix: the executor now lives in AlRunner.QueryJoin.dll, loaded LAZILY via reflection
//   on the first multi-dataitem query, so its (reflection-only) fixups never participate
//   in al-runner.dll's startup R2R binding. This shim itself references NO Ncl types in
//   its own IL — every Ncl touch is via reflection / MethodInfo.Invoke — and bridges to
//   the executor through the JoinContext delegate boundary. The minimal-insert canary
//   passes again with R2R ON.
//
//   This shim and the executor are OUR OWN assemblies (precompiled-dll-respect allows us
//   to change them freely); we touch no BC-DLL method body.
using System.Collections;
using System.Reflection;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    // queryDefinition (NCLMetaQueryDefinition) → DataAccessSource that owns the in-memory
    // tables. Stashed by GetDataAccessForQuery so the projection layer can read sibling
    // dataitem tables when it executes a join.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> _joinSourceByQueryDef = new();

    // ── lazily-loaded executor handles (resolved by reflection so no compile-time ref to
    //    the AlRunner.QueryJoin assembly leaks Ncl-touching IL into al-runner's startup) ──
    private static Assembly? _joinAsm;
    private static Type? _tJoinExecutor;
    private static Type? _tJoinContext;
    private static MethodInfo? _mExecute;
    private static MethodInfo? _mIsMultiDataItem;
    private static object? _joinCtx;
    private static readonly object _joinLoadLock = new();

    private static void EnsureJoinExecutorLoaded()
    {
        if (_mExecute != null) return;
        lock (_joinLoadLock)
        {
            if (_mExecute != null) return;
            // The isolated assembly ships beside al-runner.dll. Load it lazily so its
            // Ncl-reflecting code is JIT/R2R-bound only now (first join), never at startup.
            var dir = Path.GetDirectoryName(typeof(RecordPatches).Assembly.Location)!;
            var path = Path.Combine(dir, "AlRunner.QueryJoin.dll");
            _joinAsm = Assembly.LoadFrom(path);
            _tJoinExecutor = _joinAsm.GetType("AlRunner.QueryJoin.JoinExecutor", throwOnError: true)!;
            _tJoinContext = _joinAsm.GetType("AlRunner.QueryJoin.JoinContext", throwOnError: true)!;
            _mIsMultiDataItem = _tJoinExecutor.GetMethod("IsMultiDataItem", BindingFlags.Public | BindingFlags.Static)!;
            _mExecute = _tJoinExecutor.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static)!;
            _joinCtx = BuildJoinContext();
        }
    }

    // Build the JoinContext, wiring its delegates to al-runner's own reflection helpers.
    // Each delegate is created from a private adapter method below. None of THIS code
    // references an Ncl type in its IL: the adapters take/return `object`/`Array` and use
    // MethodInfo.Invoke / the existing reflection caches under the hood.
    private static object BuildJoinContext()
    {
        var ctx = Activator.CreateInstance(_tJoinContext!)!;
        void Set(string field, Type delType, string adapterMethod)
        {
            var mi = typeof(RecordPatches).GetMethod(adapterMethod, BindingFlags.NonPublic | BindingFlags.Static)!;
            var del = Delegate.CreateDelegate(delType, mi);
            _tJoinContext!.GetField(field, BindingFlags.Public | BindingFlags.Instance)!.SetValue(ctx, del);
        }
        // Resolve the delegate field types off the JoinContext so we never name them here.
        Type FieldType(string f) => _tJoinContext!.GetField(f, BindingFlags.Public | BindingFlags.Instance)!.FieldType;

        Set("GetDataAccessForTable", FieldType("GetDataAccessForTable"), nameof(Join_GetDataAccessForTable));
        Set("GetDataProvider", FieldType("GetDataProvider"), nameof(Join_GetDataProvider));
        Set("EnsureProjectionReflection", FieldType("EnsureProjectionReflection"), nameof(Join_EnsureProjectionReflection));
        Set("FindImplementation", FieldType("FindImplementation"), nameof(Join_FindImplementation));
        Set("BuildFindAllRequest", FieldType("BuildFindAllRequest"), nameof(Join_BuildFindAllRequest));
        Set("MakeReadOnlyRecordBuffer", FieldType("MakeReadOnlyRecordBuffer"), nameof(Join_MakeReadOnlyRecordBuffer));
        Set("ToNavValueArray", FieldType("ToNavValueArray"), nameof(Join_ToNavValueArray));
        Set("TypedDefaultForField", FieldType("TypedDefaultForField"), nameof(Join_TypedDefaultForField));
        Set("Log", FieldType("Log"), nameof(Join_Log));
        Set("OutOfScope", FieldType("OutOfScope"), nameof(Join_OutOfScope));
        return ctx;
    }

    // ── JoinContext adapters (object-typed; no Ncl type appears in this IL) ─────────
    private static object Join_GetDataAccessForTable(object source, object table)
    {
        // NavDataAccessSource_GetDataAccessForTable(object, NCLMetaTable, bool) — call via
        // reflection so this frame holds no NCLMetaTable token.
        _mGetDataAccessForTableShim ??= typeof(RecordPatches).GetMethod(
            "NavDataAccessSource_GetDataAccessForTable", BindingFlags.Public | BindingFlags.Static)!;
        return _mGetDataAccessForTableShim.Invoke(null, new object?[] { source, table, false })!;
    }
    private static MethodInfo? _mGetDataAccessForTableShim;

    private static object? Join_GetDataProvider(object dataAccess)
        => _pDataAccessDataProvider!.GetValue(dataAccess);

    private static void Join_EnsureProjectionReflection(object provider)
        => EnsureQueryProjectionReflection(provider);

    private static IEnumerable Join_FindImplementation(object provider, object request)
        => (IEnumerable)_mTtdpFindImpl!.Invoke(provider, new[] { request })!;

    private static object? Join_BuildFindAllRequest(object provider, object dataItem, object table)
        => BuildTableFindAllRequest(provider, dataItem, table);

    private static object Join_MakeReadOnlyRecordBuffer(object metaQuery, Array navValues)
        => _ctorReadOnlyRecordBuffer!.Invoke(new object?[] { metaQuery, navValues })!;

    private static Array Join_ToNavValueArray(object?[] values) => ToNavValueArray(values);

    private static void Join_Log(string m) => QLog(m);

    private static Exception Join_OutOfScope(string api, string reason)
        => new AlRunner.Infrastructure.RunnerOutOfScopeException(api, reason);

    // Produce a typed default NavValue (boxed as object) for an NCLMetaField, matching the
    // field's type — used to fill unmatched LeftOuterJoin child columns. BC projects a
    // SQL-NULL child column to the column's TYPED default (EntryNo→0, Amount→0, etc.), and
    // the engine mints exactly that via the internal NavValue.GetDefaultNavValue(metadata,
    // nullSupport:false). NCLMetaField implements INavValueMetadata, so it IS the metadata
    // argument. We call that same factory by reflection so the LeftOuter slot carries a real,
    // correctly-typed NavValue (never a null slot, which NREs NavQuery.GetColumnValue).
    private static MethodInfo? _mGetDefaultNavValue;
    private static object? Join_TypedDefaultForField(object field)
    {
        var nclAsm = field.GetType().Assembly;
        var tNavValue = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavValue")!;
        var tMeta = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.INavValueMetadata")!;
        _mGetDefaultNavValue ??= tNavValue.GetMethod("GetDefaultNavValue",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static,
            binder: null, types: new[] { tMeta, typeof(bool) }, modifiers: null)
            ?? throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                "NavQuery (multi-dataitem join)",
                "query-join-leftouter-default — NavValue.GetDefaultNavValue(INavValueMetadata,bool) " +
                "not found; cannot mint a typed default for an unmatched LeftOuterJoin child column; " +
                "see docs/scope.md");
        // nullSupport:false → the field's typed DEFAULT (0 / '' / 0D), matching BC's NULL→default
        // projection for a non-nullable query column.
        return _mGetDefaultNavValue.Invoke(null, new object?[] { field, false });
    }

    /// <summary>True iff this query definition has more than one (flat) dataitem — i.e. a join.</summary>
    private static bool IsMultiDataItemQuery(object queryDefinition)
    {
        EnsureJoinExecutorLoaded();
        return (bool)_mIsMultiDataItem!.Invoke(null, new[] { queryDefinition })!;
    }

    /// <summary>Record which DataAccessSource owns the tables for this join query.</summary>
    private static void StashJoinSource(object queryDefinition, object dataAccessSource)
    {
        _joinSourceByQueryDef.Remove(queryDefinition);
        _joinSourceByQueryDef.Add(queryDefinition, dataAccessSource);
    }

    /// <summary>
    /// Execute the join described by <paramref name="nclMetaQuery"/> over the in-memory tables
    /// and return the query-projected ReadOnlyRecordBuffers, by delegating to the lazily-loaded
    /// AlRunner.QueryJoin executor. Materialised eagerly inside the executor so any failure
    /// surfaces as a managed exception here, never a native crash mid-enumeration.
    /// </summary>
    private static IEnumerable ExecuteJoinQuery(object nclMetaQuery)
    {
        EnsureJoinExecutorLoaded();
        var queryDef = _tNCLMetaQuery!.GetProperty("QueryDefinition", BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(nclMetaQuery)!;
        if (!_joinSourceByQueryDef.TryGetValue(queryDef, out var dataAccessSource))
            throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                "NavQuery (multi-dataitem join)",
                "query-join-no-source — internal: join DataAccessSource was not stashed; see docs/scope.md");

        try
        {
            return (IEnumerable)_mExecute!.Invoke(null, new[] { _joinCtx, nclMetaQuery, dataAccessSource })!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException; // surface the executor's real exception (e.g. OOS)
        }
    }

    // ── FindProviderRequest builder for a full table scan honouring the dataitem's own
    //    filters. Pure reflection; lives here (not in the executor) so the executor needs
    //    no FindProviderRequest knowledge. Ported from the original QueryJoin.cs. ────────
    private static ConstructorInfo? _ctorFindProviderRequestAll;
    private static int _normalFindOrdinal = -1;

    private static int NormalFindOrdinal()
    {
        if (_normalFindOrdinal < 0)
        {
            try { _normalFindOrdinal = Convert.ToInt32(Enum.Parse(_tFindTypeEnum!, "Normal")); }
            catch { _normalFindOrdinal = 0; }
        }
        return _normalFindOrdinal;
    }

    private static object? BuildTableFindAllRequest(object provider, object dataItem, object table)
    {
        var nclAsm = provider.GetType().Assembly;
        const string rt = "Microsoft.Dynamics.Nav.Runtime.";
        var tFindReq = nclAsm.GetType(rt + "FindProviderRequest")!;
        _ctorFindProviderRequestAll ??= tFindReq.GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Length >= 13
                && c.GetParameters()[1].ParameterType.Name == "NCLMetaApplicationObject");
        if (_ctorFindProviderRequestAll == null) return null;

        object? StaticMember(string typeName, string member)
        {
            var t = nclAsm.GetType(rt + typeName)!;
            return t.GetField(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
                ?? t.GetProperty(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        }

        object? filtersAndMarks = null;
        try
        {
            var p = dataItem.GetType().GetProperty("TableFiltersAndMarks",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            filtersAndMarks = p?.GetValue(dataItem);
        }
        catch { filtersAndMarks = null; }
        filtersAndMarks ??= StaticMember("FiltersAndMarks", "Empty");

        var emptyTfd = StaticMember("TableFilterDictionary", "Empty");
        var ps = _ctorFindProviderRequestAll.GetParameters();
        var args = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            args[i] = ps[i].Name switch
            {
                "companyToken" => 0,
                "metaApplicationObject" => table,                 // table-shaped: no projection
                "lockState" => Enum.ToObject(ps[i].ParameterType, 0),
                "filtersAndMarks" => filtersAndMarks,
                "globalAndSecurityFilters" => emptyTfd,
                "flowFieldSecurityFiltering" => ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null,
                "autoCalcFields" => null,
                "sortingFields" => null,
                "findType" => Enum.ToObject(_tFindTypeEnum!, NormalFindOrdinal()),
                "topNumberOfRowsToReturn" => 0,
                "skipNumberOfRows" => 0,
                "fastNumberOfRowsToReturn" => 0,
                "timeout" => null,
                "fieldLoadInfo" => null,
                _ => ps[i].HasDefaultValue ? ps[i].DefaultValue : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null)
            };
        }
        return _ctorFindProviderRequestAll.Invoke(args);
    }
}
