// RecordPatches.FieldFindIntercept — managed find interception for the virtual Field
// system table (2000000041).
//
// WHY THIS EXISTS
//   With the in-memory store populated (see RecordPatches.FieldVirtualTable.cs) and the
//   metatable's Virtual bit cleared, the AL `Field.SetRange(TableNo,<t>); Field.FindSet()`
//   STILL SIGSEGVs (exit 139) inside BC's R2R-precompiled DataAccess.InnerFindAsync async
//   state machine BEFORE it ever calls provider.FindAsync.
//
//   ROOT CAUSE (file-probe + decompile evidence, 2026-05-31):
//   InnerFindAsync (Ncl.dll DataAccess) runs, for EVERY find, a SQL transactional-cache
//   prologue BEFORE delegating to the provider:
//     1. TryHandleAsPrimaryKeyOrSystemIdCacheLookupAsync(request)  — reads the per-table
//        SystemId/PrimaryKey caches keyed by request.MetaApplicationObject.ObjectId.
//     2. transactionalDataCache.TryFind(request, …)               — GetLockAndSessionState /
//        GetGlobalTableVersion / EnsureReadTransactionStarted, all keyed by that ObjectId.
//   These per-object SQL cache/transaction/table-version structures are allocated for real
//   tables but NOT for the virtual system table 2000000041 (which the service tier serves
//   from a dedicated VirtualDataProvider that never enters this cache). On the skeleton the
//   native R2R code dereferences the missing per-object structure and AVs.
//
//   PROOF the crash is in this native prologue, not our rows or metatable shape:
//   our Cecil-rewritten managed TempTableDataProvider.Find is reached 0× for 2000000041;
//   the crash reproduces even with ZERO rows inserted AND with TableType forced to
//   Temporary (SqlHasSystemId=False, SystemId lookup skipped) — so it is intrinsic to the
//   find machinery handling a request whose MetaApplicationObject is the 2000000041
//   metatable, independent of our populate and of the SystemId/PK branch.
//
// WHAT THIS DOES (faithful, managed — mirrors the QueryJoin take-over-higher approach)
//   We Cecil-redirect DataAccess.FindAsync / DataAccess.FindFromPositionAsync to the two
//   helpers below. For table 2000000041 ONLY, the helper bypasses the crashing native
//   InnerFindAsync prologue and reproduces its SAFE tail in managed code:
//     - build the FindProviderRequest from the FindCacheRequest (exactly as InnerFindAsync
//       line 735 does, lockState=None),
//     - call provider.Find (our managed TempTableDataProvider_Find — the genuine in-memory
//       filter/sort over the populated Field rows),
//     - wrap the result in a ResultSet + ResultSetEnumerator (the same two types
//       InnerFindAsync builds at its tail), with an always-valid still-valid function
//       (our in-memory store is not concurrently invalidated within a test).
//   For EVERY OTHER table the helper invokes the ORIGINAL private InnerFindAsync unchanged,
//   so all non-2000000041 finds keep BC's exact native behaviour (cache, version tokens,
//   transactions). This is a no-op on the 99.99% normal-table path.
//
// PRECOMPILED-DLL RESPECT / R2R-TOKEN-SAFETY
//   We touch no BC business-logic body. DataAccess/ResultSet/ResultSetEnumerator/
//   FindProviderRequest are runtime-engine types we invoke by reflection. The Cecil rewrite
//   reuses Ncl's existing memberRefs only (imports our own helper ref + an unbox.any of the
//   target's own return type) — no new typeRefs/memberRefs added to Ncl.
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    private static bool _ffiReady;
    private static MethodInfo? _mInnerFindAsync;          // DataAccess.InnerFindAsync(FindCacheRequest, bool, Func<bool>)
    private static PropertyInfo? _pReqMetaAppObj_Find;    // DataCacheRequest.MetaApplicationObject
    private static PropertyInfo? _pMaoObjectId;           // NCLMetaApplicationObject.ObjectId
    private static MethodInfo? _mProviderFind;            // SynchronousDataProvider.Find(FindProviderRequest, Func<bool>)
    private static MethodInfo? _mProviderFindFromPos;     // SynchronousDataProvider.FindFromPosition(PositionedFindProviderRequest, Func<bool>)
    private static ConstructorInfo? _ctorFindProviderReq; // FindProviderRequest(14 args)
    private static ConstructorInfo? _ctorResultSet;       // ResultSet(IAsyncEnumerator, FindCacheRequest, int, int, bool)
    private static ConstructorInfo? _ctorResultSetEnum;   // ResultSetEnumerator(ResultSet, NavSession, DataAccess, int, Func<bool>)
    private static PropertyInfo? _pDaSession;             // DataAccess.session (NavSession)
    private static PropertyInfo? _pDaProvider2;           // DataAccess.DataProvider
    private static PropertyInfo? _pProviderShouldBuffer;  // DataProvider.ShouldResultSetBufferRows
    private static MethodInfo? _mAsAsyncEnumerable;       // AsyncEnumerableExtensions.AsAsyncEnumerable<T>(IEnumerable<T>) → IAsyncEnumerable<T>
    private static MethodInfo? _mGetAsyncEnumerator;      // IAsyncEnumerable<ReadOnlyRecordBuffer>.GetAsyncEnumerator(CancellationToken)
    private static PropertyInfo? _pSessionCancellation;   // NavSession.CancellationToken
    private static object? _findCacheReqType;             // typeof FindCacheRequest
    private static Type? _tReadOnlyRecBufFfi;

    // Cache request property accessors used to translate FindCacheRequest → FindProviderRequest.
    private static PropertyInfo? _pCompanyToken, _pFiltersAndMarks, _pGlobalAndSecurityFilters,
        _pFlowFieldSecurityFiltering, _pAutoCalcFields, _pSortingFields, _pFindTypeProp,
        _pTopRows, _pSkipRows, _pFastRows, _pTimeout, _pFieldLoadInfo;
    private static object? _dataLockStateNone;

    private const int FieldFindTableId = FieldVirtualTableId; // 2000000041

    /// <summary>
    /// Replacement for DataAccess.FindAsync(FindCacheRequest, Func&lt;bool&gt;).
    /// Returns a boxed ValueTask&lt;ResultSetEnumerator&gt; (the Cecil rewrite unbox.any's it
    /// back to the declared return type). For 2000000041 → managed bypass; else → original
    /// InnerFindAsync(request, fromPosition:false, onlyKeyNeeded).
    /// </summary>
    /// <summary>
    /// Field-table (2000000041) managed find. Called from a branch PREPENDED to
    /// DataAccess.InnerFindAsync (see NclCecilRewrite) that fires ONLY when
    /// request.MetaApplicationObject.ObjectId == 2000000041; every other table falls through to
    /// the original InnerFindAsync IL untouched. Returns a boxed
    /// ValueTask&lt;ResultSetEnumerator&gt; (the prepended IL unbox.any's it to the declared
    /// return type).
    ///
    /// We prepend to InnerFindAsync (not FindAsync) because under default R2R the tiny
    /// DataAccess.FindAsync (`return InnerFindAsync(...)`) is INLINED into its callers
    /// (RecordImplementation.IssueFindRequestAsync), so a Cecil rewrite of FindAsync is
    /// R2R-inlined-past and never fires (file-traced: hit under AL_RUNNER_NCL_CACHE=0 / pure-JIT,
    /// NOT under default R2R). InnerFindAsync is a large async state machine that is NOT inlined,
    /// so the prepended branch is reached on both the JIT and R2R paths. See
    /// .claude/rules / feedback_r2r_inlining_traps.
    /// </summary>
    public static object DataAccess_FieldFindManaged(object self, object request, bool fromPosition)
    {
        EnsureFindInterceptReflection(self, request);
        return BuildManagedFindResult(self, request, fromPosition, onlyKeyNeeded: null);
    }

    /// <summary>True iff this find request targets the virtual Field table (2000000041).
    /// Used by the prepended IL guard.</summary>
    // Lightweight per-find reflection for the hot predicate ONLY (does NOT trigger the full
    // EnsureFindInterceptReflection — that heavy bind is deferred to FieldFindManaged, which runs
    // only for the Field table). Resolving the heavy reflection on every corpus/query find both
    // wastes time and (observed) destabilises the find path. These two accessors are cheap and
    // resolved once.
    private static PropertyInfo? _pReqMaoLight;   // DataCacheRequest.MetaApplicationObject
    private static PropertyInfo? _pMaoObjIdLight;  // NCLMetaApplicationObject.ObjectId
    private static volatile bool _lightReady;

    public static bool DataAccess_IsFieldFindRequest(object self, object request)
        => IsFieldFindRequest(request);

    private static bool IsFieldFindRequest(object request)
    {
        try
        {
            if (!_lightReady)
            {
                var nclAsm = request.GetType().Assembly;
                const string rt = "Microsoft.Dynamics.Nav.Runtime.";
                _pReqMaoLight = nclAsm.GetType(rt + "DataCacheRequest")!
                    .GetProperty("MetaApplicationObject", BindingFlags.Public | BindingFlags.Instance);
                _pMaoObjIdLight = nclAsm.GetType(rt + "NCLMetaApplicationObject")!
                    .GetProperty("ObjectId", BindingFlags.Public | BindingFlags.Instance);
                _lightReady = _pReqMaoLight != null && _pMaoObjIdLight != null;
                if (!_lightReady) return false;
            }
            var mao = _pReqMaoLight!.GetValue(request);
            if (mao == null) return false;
            var oid = _pMaoObjIdLight!.GetValue(mao);
            return oid is int i && i == FieldFindTableId;
        }
        catch { return false; }
    }

    /// <summary>
    /// Reproduce InnerFindAsync's SAFE tail for the Field table in managed code:
    /// provider.Find(FindProviderRequest) → ResultSet → ResultSetEnumerator, wrapped in a
    /// completed ValueTask&lt;ResultSetEnumerator&gt;.
    /// </summary>
    private static object BuildManagedFindResult(object dataAccess, object cacheRequest, bool fromPosition, Func<bool>? onlyKeyNeeded)
    {
        var session = _pDaSession!.GetValue(dataAccess)!;
        var provider = _pDaProvider2!.GetValue(dataAccess)!;

        // On-demand populate: the AL `Field.SetRange(TableNo,<t>)` filter is known only now (at
        // find time), and the source table <t> may not have been materialised when the data
        // access was first acquired. Build + insert <t>'s real field rows into the store before
        // the find runs, so the filter narrows to a populated set. Idempotent per (provider,t).
        EnsureFilteredFieldTablePopulated(dataAccess, cacheRequest, session);

        // Build the provider request exactly as InnerFindAsync line 735 (lockState = None).
        IEnumerable rawRows;
        if (!fromPosition)
        {
            var findProviderReq = BuildFindProviderRequest(cacheRequest);
            rawRows = (IEnumerable)_mProviderFind!.Invoke(provider, new object?[] { findProviderReq, onlyKeyNeeded })!;
        }
        else
        {
            // Positioned find: the AL Next() path. The temp provider's FindFromPosition
            // honours the starting position + sort. We translate the positioned cache
            // request to a positioned provider request below.
            var posProviderReq = BuildPositionedFindProviderRequest(cacheRequest);
            rawRows = (IEnumerable)_mProviderFindFromPos!.Invoke(provider, new object?[] { posProviderReq, onlyKeyNeeded })!;
        }

        // IEnumerable<ReadOnlyRecordBuffer> → IAsyncEnumerable<ReadOnlyRecordBuffer> → enumerator.
        // BC's IAsyncEnumerable.GetAsyncEnumerator overload takes a NavCancellationToken
        // (session.CancellationToken), NOT System.Threading.CancellationToken — exactly as
        // InnerFindAsync line 742 does (readOnlyRecordBuffers.GetAsyncEnumerator(session.CancellationToken)).
        var asyncEnumerable = _mAsAsyncEnumerable!.Invoke(null, new object?[] { rawRows })!;
        var navCancellation = _pSessionCancellation!.GetValue(session)!;
        EnsureGetAsyncEnumerator(asyncEnumerable, navCancellation);
        // Pass the token type the resolved overload actually wants: BC's overload takes
        // NavCancellationToken (session.CancellationToken); the BCL overload takes
        // System.Threading.CancellationToken (default is fine — no cancellation in-test).
        var wantType = _mGetAsyncEnumerator!.GetParameters()[0].ParameterType;
        object tokenArg = wantType.IsInstanceOfType(navCancellation)
            ? navCancellation
            : (wantType == typeof(System.Threading.CancellationToken)
                ? default(System.Threading.CancellationToken)
                : System.Activator.CreateInstance(wantType)!);
        var asyncEnumerator = _mGetAsyncEnumerator!.Invoke(asyncEnumerable, new[] { tokenArg })!;

        bool shouldBuffer = (bool)_pProviderShouldBuffer!.GetValue(provider)!;

        // ResultSet(enumerator, findRequest, overallTableVersion=0, tableVersion=0, bufferResults).
        // Version tokens are 0 (no global table-version infra for the virtual table); the
        // still-valid function below always returns true, so the result set is never spuriously
        // invalidated. Faithful for an in-memory store that is not concurrently mutated within
        // a single test's find loop.
        var resultSet = _ctorResultSet!.Invoke(new object?[] { asyncEnumerator, cacheRequest, 0, 0, shouldBuffer })!;

        Func<bool> alwaysValid = static () => true;
        var rse = _ctorResultSetEnum!.Invoke(new object?[] { resultSet, session, dataAccess, 0, alwaysValid })!;

        // Box into ValueTask<ResultSetEnumerator> via ValueTask.FromResult through reflection.
        return WrapInValueTask(rse);
    }

    private static PropertyInfo? _pFamFilters;        // FiltersAndMarks.Filters
    private static PropertyInfo? _pFfdItems;          // FilterFieldDictionary.Items (Tuple<INavFieldMetadata,FilterExpression>[])
    private static PropertyInfo? _pUnaryValue, _pFilterExprType, _pFieldNo;
    private static Type? _ffiUnaryFilterExpr;
    private static MethodInfo? _mGetDataAccessProviderForPopulate;

    /// <summary>
    /// Extract the `TableNo` equality filter (Field table field No. 1) from the find request and
    /// ensure that source table's real field rows are populated into the store. The populate is
    /// the same faithful FieldDataProvider.GetFieldRecordBuffer path used in
    /// PopulateFieldVirtualTable, but targeted at exactly the filtered TableNo (the only table the
    /// find can return). Idempotent (PopulateFieldVirtualTable tracks done-tables per provider).
    /// </summary>
    private static void EnsureFilteredFieldTablePopulated(object dataAccess, object cacheRequest, object session)
    {
        try
        {
            var fieldMetaTable = (NCLMetaTable)_pReqMetaAppObj_Find!.GetValue(cacheRequest)!;
            int? filteredTableNo = ExtractTableNoFilter(cacheRequest);

            // Always run the generic populate (materialised tables) so a bare Field.FindSet()
            // with no TableNo filter still enumerates everything currently known; cheap+idempotent.
            PopulateFieldVirtualTable(dataAccess, fieldMetaTable, session);

            // Targeted: if a specific TableNo is filtered and not yet built, build+insert it now.
            if (filteredTableNo is int t && t > 0)
            {
                NCLMetaTable? srcMeta;
                try { srcMeta = EnsureTableInMetadataCache(t); }
                catch { srcMeta = null; }
                if (srcMeta != null)
                {
                    EnsureDataAccessProviderReflection(dataAccess);
                    var provider = _pDataAccessDataProvider!.GetValue(dataAccess)!;
                    var done = _fvtPopulatedByProvider.GetValue(provider, static _ => new ConcurrentDictionary<int, byte>());
                    if (done.TryAdd(t, 0))
                        InsertFieldRowsForTable(provider, fieldMetaTable, srcMeta);
                }
            }
        }
        catch (AlRunner.Infrastructure.RunnerOutOfScopeException) { throw; }
        catch { /* best-effort populate; the find still runs over whatever is present */ }
    }

    private static int? ExtractTableNoFilter(object cacheRequest)
    {
        EnsureFilterReflection(cacheRequest);
        var fam = _pFiltersAndMarks!.GetValue(cacheRequest);
        if (fam == null) return null;
        var filters = _pFamFilters!.GetValue(fam);
        if (filters == null) return null;
        var items = _pFfdItems!.GetValue(filters) as Array;
        if (items == null) return null;
        foreach (var item in items)
        {
            // item is Tuple<INavFieldMetadata, FilterExpression>
            var tupleType = item!.GetType();
            var fieldMeta = tupleType.GetProperty("Item1")!.GetValue(item);
            var expr = tupleType.GetProperty("Item2")!.GetValue(item);
            if (fieldMeta == null || expr == null) continue;
            // Field table field No. 1 == "TableNo".
            var fieldNoObj = _pFieldNo?.GetValue(fieldMeta);
            if (fieldNoObj is not int fieldNo || fieldNo != 1) continue;
            // Only an equality filter pins a single table.
            if (!_ffiUnaryFilterExpr!.IsInstanceOfType(expr)) continue;
            var exprTypeVal = _pFilterExprType!.GetValue(expr);
            if (exprTypeVal?.ToString() != "Equal") continue;
            var navValue = _pUnaryValue!.GetValue(expr);
            var asInt = NavValueToInt(navValue);
            if (asInt is int n) return n;
        }
        return null;
    }

    private static int? NavValueToInt(object? navValue)
    {
        if (navValue == null) return null;
        // NavInteger exposes a Value (int) / ToInt32; try common accessors.
        var t = navValue.GetType();
        var vProp = t.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
        if (vProp != null)
        {
            var v = vProp.GetValue(navValue);
            if (v is int vi) return vi;
            if (v != null && int.TryParse(v.ToString(), out var pv)) return pv;
        }
        var toInt = t.GetMethod("ToInt32", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        if (toInt != null)
        {
            var r = toInt.Invoke(navValue, null);
            if (r is int ri) return ri;
        }
        if (int.TryParse(navValue.ToString(), out var sp)) return sp;
        return null;
    }

    private static bool _filterReflReady;
    private static void EnsureFilterReflection(object cacheRequest)
    {
        if (_filterReflReady) return;
        var nclAsm = cacheRequest.GetType().Assembly;
        const string rt = "Microsoft.Dynamics.Nav.Runtime.";
        var tFam = nclAsm.GetType(rt + "FiltersAndMarks")!;
        _pFamFilters = tFam.GetProperty("Filters", BindingFlags.Public | BindingFlags.Instance);
        var tFfdBase = nclAsm.GetType(rt + "FilterFieldDictionary")!;
        // Items is on the base dictionary type; search the hierarchy.
        _pFfdItems = tFfdBase.GetProperty("Items", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var bt = tFfdBase.BaseType;
        while (_pFfdItems == null && bt != null)
        {
            _pFfdItems = bt.GetProperty("Items", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            bt = bt.BaseType;
        }
        _ffiUnaryFilterExpr = nclAsm.GetType(rt + "UnaryFilterExpression")!;
        _pUnaryValue = _ffiUnaryFilterExpr.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
        var tFilterExpr = nclAsm.GetType(rt + "FilterExpression")!;
        _pFilterExprType = tFilterExpr.GetProperty("ExpressionType", BindingFlags.Public | BindingFlags.Instance);
        // NCLMetaField.FieldNo / No (the field's AL number). Field No. 1 == TableNo on table 2000000041.
        var tMetaField = nclAsm.GetType(rt + "NCLMetaField")!;
        _pFieldNo = tMetaField.GetProperty("FieldNo", BindingFlags.Public | BindingFlags.Instance)
            ?? tMetaField.GetProperty("No", BindingFlags.Public | BindingFlags.Instance);
        _filterReflReady = _pFamFilters != null && _pFfdItems != null && _pFieldNo != null;
    }

    private static MethodInfo? _mValueTaskFromResult; // ValueTask.FromResult<ResultSetEnumerator>
    private static object WrapInValueTask(object resultSetEnumerator)
    {
        // ValueTask.FromResult<T>(T) where T = ResultSetEnumerator. Returns ValueTask<ResultSetEnumerator>
        // boxed as object; the Cecil rewrite unbox.any's it to the declared return type.
        return _mValueTaskFromResult!.Invoke(null, new[] { resultSetEnumerator })!;
    }

    private static object BuildFindProviderRequest(object cacheReq)
    {
        var args = new object?[]
        {
            _pCompanyToken!.GetValue(cacheReq),
            _pReqMetaAppObj_Find!.GetValue(cacheReq),
            _dataLockStateNone,
            _pFiltersAndMarks!.GetValue(cacheReq),
            _pGlobalAndSecurityFilters!.GetValue(cacheReq),
            _pFlowFieldSecurityFiltering!.GetValue(cacheReq),
            _pAutoCalcFields!.GetValue(cacheReq),
            _pSortingFields!.GetValue(cacheReq),
            _pFindTypeProp!.GetValue(cacheReq),
            _pTopRows!.GetValue(cacheReq),
            _pSkipRows!.GetValue(cacheReq),
            _pFastRows!.GetValue(cacheReq),
            _pTimeout!.GetValue(cacheReq),
            _pFieldLoadInfo!.GetValue(cacheReq),
        };
        return _ctorFindProviderReq!.Invoke(args)!;
    }

    private static ConstructorInfo? _ctorPosFindProviderReq;
    private static PropertyInfo? _pStartingPosition, _pIncludeCurrent;
    private static object BuildPositionedFindProviderRequest(object cacheReq)
    {
        // PositionedFindProviderRequest(companyToken, mao, lockState, filtersAndMarks,
        //   globalAndSecurity, flowFieldSec, autoCalc, sortingFields, findType,
        //   startingPosition, includeCurrent, topRows, skipRows, fastRows, timeout, fieldLoadInfo)
        var args = new object?[]
        {
            _pCompanyToken!.GetValue(cacheReq),
            _pReqMetaAppObj_Find!.GetValue(cacheReq),
            _dataLockStateNone,
            _pFiltersAndMarks!.GetValue(cacheReq),
            _pGlobalAndSecurityFilters!.GetValue(cacheReq),
            _pFlowFieldSecurityFiltering!.GetValue(cacheReq),
            _pAutoCalcFields!.GetValue(cacheReq),
            _pSortingFields!.GetValue(cacheReq),
            _pFindTypeProp!.GetValue(cacheReq),
            _pStartingPosition!.GetValue(cacheReq),
            _pIncludeCurrent!.GetValue(cacheReq),
            _pTopRows!.GetValue(cacheReq),
            _pSkipRows!.GetValue(cacheReq),
            _pFastRows!.GetValue(cacheReq),
            _pTimeout!.GetValue(cacheReq),
            _pFieldLoadInfo!.GetValue(cacheReq),
        };
        return _ctorPosFindProviderReq!.Invoke(args)!;
    }

    private static void EnsureFindInterceptReflection(object dataAccess, object request)
    {
        if (_ffiReady) return;
        lock (typeof(RecordPatches))
        {
            if (_ffiReady) return;
            var nclAsm = dataAccess.GetType().Assembly;
            const string rt = "Microsoft.Dynamics.Nav.Runtime.";

            var tDataAccess = nclAsm.GetType(rt + "DataAccess")!;
            _mInnerFindAsync = tDataAccess.GetMethod("InnerFindAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("DataAccess.InnerFindAsync not found");
            _pDaSession = tDataAccess.GetProperty("Session", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("DataAccess.Session not found");
            _pDaProvider2 = tDataAccess.GetProperty("DataProvider", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("DataAccess.DataProvider not found");

            var tDataCacheReq = nclAsm.GetType(rt + "DataCacheRequest")!;
            _pReqMetaAppObj_Find = tDataCacheReq.GetProperty("MetaApplicationObject", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException("DataCacheRequest.MetaApplicationObject not found");
            _pCompanyToken = tDataCacheReq.GetProperty("CompanyToken", BindingFlags.Public | BindingFlags.Instance);
            _pFiltersAndMarks = tDataCacheReq.GetProperty("FiltersAndMarks", BindingFlags.Public | BindingFlags.Instance);
            _pGlobalAndSecurityFilters = tDataCacheReq.GetProperty("GlobalAndSecurityFilters", BindingFlags.Public | BindingFlags.Instance);
            _pFlowFieldSecurityFiltering = tDataCacheReq.GetProperty("FlowFieldSecurityFiltering", BindingFlags.Public | BindingFlags.Instance);
            _pFieldLoadInfo = tDataCacheReq.GetProperty("FieldLoadInfo", BindingFlags.Public | BindingFlags.Instance);

            var tAutoCalcReq = nclAsm.GetType(rt + "AutoCalcFieldsCacheRequest")!;
            _pAutoCalcFields = tAutoCalcReq.GetProperty("AutoCalcFields", BindingFlags.Public | BindingFlags.Instance);

            var tFindCacheReq = nclAsm.GetType(rt + "FindCacheRequest")!;
            _pSortingFields = tFindCacheReq.GetProperty("SortingFields", BindingFlags.Public | BindingFlags.Instance);
            _pFindTypeProp = tFindCacheReq.GetProperty("FindType", BindingFlags.Public | BindingFlags.Instance);
            _pTopRows = tFindCacheReq.GetProperty("TopNumberOfRowsToReturn", BindingFlags.Public | BindingFlags.Instance);
            _pSkipRows = tFindCacheReq.GetProperty("SkipNumberOfRows", BindingFlags.Public | BindingFlags.Instance);
            _pFastRows = tFindCacheReq.GetProperty("FastNumberOfRowsToReturn", BindingFlags.Public | BindingFlags.Instance);
            _pTimeout = tFindCacheReq.GetProperty("Timeout", BindingFlags.Public | BindingFlags.Instance);

            var tPosFindCacheReq = nclAsm.GetType(rt + "PositionedFindCacheRequest")!;
            _pStartingPosition = tPosFindCacheReq.GetProperty("StartingPosition", BindingFlags.Public | BindingFlags.Instance);
            _pIncludeCurrent = tPosFindCacheReq.GetProperty("IncludeCurrent", BindingFlags.Public | BindingFlags.Instance);

            var tMao = nclAsm.GetType(rt + "NCLMetaApplicationObject")!;
            _pMaoObjectId = tMao.GetProperty("ObjectId", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException("NCLMetaApplicationObject.ObjectId not found");

            var tSyncProvider = nclAsm.GetType(rt + "SynchronousDataProvider")!;
            var tFindProviderReq = nclAsm.GetType(rt + "FindProviderRequest")!;
            var tPosFindProviderReq = nclAsm.GetType(rt + "PositionedFindProviderRequest")!;
            var tFunc = typeof(Func<bool>);
            _mProviderFind = tSyncProvider.GetMethod("Find", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { tFindProviderReq, tFunc }, null)
                ?? throw new InvalidOperationException("SynchronousDataProvider.Find not found");
            _mProviderFindFromPos = tSyncProvider.GetMethod("FindFromPosition", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { tPosFindProviderReq, tFunc }, null)
                ?? throw new InvalidOperationException("SynchronousDataProvider.FindFromPosition not found");

            _ctorFindProviderReq = tFindProviderReq.GetConstructors()
                .First(c => c.GetParameters().Length == 14);
            _ctorPosFindProviderReq = tPosFindProviderReq.GetConstructors()
                .First(c => c.GetParameters().Length == 16);

            var tResultSet = nclAsm.GetType(rt + "ResultSet")!;
            _ctorResultSet = tResultSet.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .First(c => c.GetParameters().Length == 5);
            var tResultSetEnum = nclAsm.GetType(rt + "ResultSetEnumerator")!;
            _ctorResultSetEnum = tResultSetEnum.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .First(c => c.GetParameters().Length == 5);

            var tDataProvider = nclAsm.GetType(rt + "DataProvider")!;
            _pProviderShouldBuffer = tDataProvider.GetProperty("ShouldResultSetBufferRows",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("DataProvider.ShouldResultSetBufferRows not found");

            _tReadOnlyRecBufFfi = nclAsm.GetType(rt + "ReadOnlyRecordBuffer")!;

            // AsAsyncEnumerable<ReadOnlyRecordBuffer>(IEnumerable<ReadOnlyRecordBuffer>) — the
            // same extension InnerFindAsync uses (provider.Find(...).AsAsyncEnumerable()).
            _mAsAsyncEnumerable = FindAsAsyncEnumerableExtension(nclAsm, _tReadOnlyRecBufFfi);

            var tNavSession = _pDaSession.PropertyType;
            _pSessionCancellation = tNavSession.GetProperty("CancellationToken", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("NavSession.CancellationToken not found");
            // _mGetAsyncEnumerator is resolved lazily once we have the concrete async-enumerable
            // type + the concrete cancellation-token type (NavCancellationToken) — see
            // EnsureGetAsyncEnumerator.

            var tDataLockState = nclAsm.GetType(rt + "DataLockState")!;
            _dataLockStateNone = Enum.Parse(tDataLockState, "None");

            // ValueTask.FromResult<ResultSetEnumerator>(ResultSetEnumerator)
            var fromResultOpen = typeof(System.Threading.Tasks.ValueTask)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == "FromResult" && m.IsGenericMethodDefinition
                    && m.GetParameters().Length == 1);
            _mValueTaskFromResult = fromResultOpen.MakeGenericMethod(tResultSetEnum);

            _ffiReady = true;
        }
    }

    private static void EnsureGetAsyncEnumerator(object asyncEnumerable, object cancellation)
    {
        if (_mGetAsyncEnumerator != null) return;
        var aeType = asyncEnumerable.GetType();
        var ctType = cancellation.GetType();
        // Prefer a GetAsyncEnumerator overload whose single param matches the runtime cancellation
        // token type (NavCancellationToken). Fall back to any single-arg overload assignable from it.
        var candidates = aeType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(m => m.Name == "GetAsyncEnumerator" && m.GetParameters().Length == 1)
            .ToArray();
        var match = candidates.FirstOrDefault(m => m.GetParameters()[0].ParameterType == ctType)
            ?? candidates.FirstOrDefault(m => m.GetParameters()[0].ParameterType.IsAssignableFrom(ctType))
            // BC's EnumerableAsyncIterator implements the standard IAsyncEnumerable<T>, whose
            // GetAsyncEnumerator takes System.Threading.CancellationToken. session.CancellationToken
            // (NavCancellationToken) converts to it; we pass default in BuildManagedFindResult.
            ?? candidates.FirstOrDefault(m => m.GetParameters()[0].ParameterType == typeof(System.Threading.CancellationToken))
            // Last resort: the parameterless interface enumerator (none expected, but be safe).
            ?? candidates.FirstOrDefault();
        if (match == null)
            throw new InvalidOperationException(
                $"GetAsyncEnumerator({ctType.Name}) not found on {aeType.FullName}");
        _mGetAsyncEnumerator = match;
    }

    private static MethodInfo FindAsAsyncEnumerableExtension(Assembly nclAsm, Type elementType)
    {
        // Locate the extension method `IEnumerable<T> → IAsyncEnumerable<T>` named
        // "AsAsyncEnumerable" used throughout Ncl. Search Ncl + its referenced asms.
        foreach (var asm in new[] { nclAsm }.Concat(AppDomain.CurrentDomain.GetAssemblies()).Distinct())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch { continue; }
            foreach (var t in types)
            {
                if (!t.IsAbstract || !t.IsSealed) continue; // static class
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "AsAsyncEnumerable" || !m.IsGenericMethodDefinition) continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 1) continue;
                    if (!ps[0].ParameterType.IsGenericType) continue;
                    if (ps[0].ParameterType.GetGenericTypeDefinition() != typeof(IEnumerable<>)) continue;
                    try { return m.MakeGenericMethod(elementType); } catch { }
                }
            }
        }
        throw new InvalidOperationException("AsAsyncEnumerable<T>(IEnumerable<T>) extension not found");
    }
}
