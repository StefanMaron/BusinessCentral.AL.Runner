// RecordWritePatches — registration AND replacements for the NavRecord write/find path
// plus the underlying TempTableDataProvider data-access plumbing.
//
// Registration: ApplyRecordPatches(navNcl) wires up every JMP-hook required for AL records
// to behave correctly in headless mode — NavRecordHandle.CreateTarget, the
// NavSession DataAccessSource/Database getters, the TempTableDataProvider ctor, the
// CollationAwareStringComparer, NavRecord.Dispose, RecordImplementation permission/security
// no-ops, the SystemId UUID hook, and the NavRecord.InsertAsync /
// InternalFindRecordWithoutCheckingValuesAsync replacements.
//
// Replacements: NavRecord.InsertAsync and InternalFindRecordWithoutCheckingValuesAsync —
// the original bodies dispatch through trigger/event/extension and permission-event
// telemetry that NREs on the skeleton session. We bypass those and call the underlying
// dataAccess directly. The TempTableDataProvider hooked up by RecordPatches.cs handles
// actual storage.
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AlRunner;

public static partial class BcRuntime
{
    /// <summary>
    /// All record / data-access JMP-hooks. Called from ApplyAllPatches once during
    /// runtime bootstrap, after NavSession / NavMethodScope / NavApplicationObjectBase
    /// have been wired up (the record path needs a usable skeleton session).
    /// </summary>
    private static void ApplyRecordPatches(Assembly navNcl)
    {
        // Locals frequently referenced below — resolved once, used many times.
        var sessType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavSession");

        // ── RECORD PATCHES (Approach A spike) ────────────────────────────────────────
        // NavRecordHandle.CreateTarget — bypass NCLMetadata by constructing Record{ID}
        // directly using an NCLMetaTable built from parsed AL source, backed by BC's own
        // TempTableDataProvider (in-memory AVL-tree store).
        AlRunner.Patches.RecordPatches.Register();

        // W-8b A-prime: resolve EventSubscriberPatches' reflection state up front so
        // EventSubscriberPatches.CreateTableTriggerEventHandler / InjectAll can run during
        // NclMetaTableBuilder / NclMetadataCachePopulator without extra plumbing.
        AlRunner.Patches.EventSubscriberPatches.Register(navNcl);

        // RecordLink (table 2000000068) in-memory polyfill — AL `Rec.AddLink/HasLinks/
        // DeleteLinks/CopyLinks` paths. Real BC body NREs in NavRecord..ctor because
        // our skeleton lacks a TenantDataAccess for system tables (see docs/scope.md §2).
        AlRunner.Patches.RecordLinkPatches.Register(navNcl);

        // NavRecordId.get_CollationAwareStringComparer — real getter walks
        // Session.Database.CollationAwareStringComparer which NREs on the skeleton
        // (NavTenant.database LazyEx is null). Hook with a cached comparer to drain
        // the NRE cluster that surfaces from TempTableDataProvider.Modify on
        // Rename/Modify paths. See Patches/NavRecordIdPatches.cs.
        AlRunner.Patches.NavRecordIdPatches.Register(navNcl);

        // IsolatedStorage / ALSystemEncryption — no JmpHook registration here (#1883):
        // BC's real, unpatched ALIsolatedStorage.AL* bodies already run (JmpHook disabled by
        // default) and delegate to IsolatedStorageRepository / ALSystemEncryption, which are
        // Cecil-rewritten onto TenantStoragePatches' in-memory store + real AES-256-CBC
        // envelope (see NclCecilRewrite.cs and TenantStoragePatches.cs for the full history).

        // Pre-populate skeleton session's DataAccessSource field directly.
        // NavSession.DataAccessSource getter is inlined by JIT (trivial field return),
        // so the JMP hook on it never fires — we must inject DAS via field reflection.
        Console.Error.WriteLine($"[BcRuntime] _skeletonSession null? {_skeletonSession == null}");
        if (_skeletonSession != null)
            AlRunner.Patches.RecordPatches.InitializeSkeletonSession(_skeletonSession);
        else
            Console.Error.WriteLine("[BcRuntime] WARN: _skeletonSession is null — DAS not injected");

        // NavRecordHandle.CreateTarget, NavSession.get_DataAccessSource, NavSession.get_Database,
        // DataAccessSource.GetDataAccessForTable, TempTableDataProvider.ctor,
        // TempTableDataProvider.CalcNumeric, and NavDatabase.get_CollationAwareStringComparer
        // are all Cecil-owned (see NclCecilRewrite.cs).

        // NavRecord.Dispose(bool) — NREs when RequiredSessionId != NavCurrentThread.Session.Id
        var navRecordType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavRecord");
        if (navRecordType != null)
        {
            // BeginInitialization / EndInitialization — hook as no-op. Called by NavRecord..ctor
            // body and may NRE via Session.MetadataProvider on the skeleton.
            var replNoOp = typeof(BcRuntime).GetMethod(nameof(NavRecord_BeginEndInitialization),
                BindingFlags.Public | BindingFlags.Static)!;
            var beginInit = navRecordType.GetMethod("BeginInitialization",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (beginInit != null)
            {
                AlRunner.Infrastructure.JmpHook.Apply(beginInit, replNoOp, "NavRecord.BeginInitialization()");
                Console.Error.WriteLine("[BcRuntime] NavRecord.BeginInitialization() hooked → NoOp");
            }
            var endInit = navRecordType.GetMethod("EndInitialization",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (endInit != null)
            {
                AlRunner.Infrastructure.JmpHook.Apply(endInit, replNoOp, "NavRecord.EndInitialization()");
                Console.Error.WriteLine("[BcRuntime] NavRecord.EndInitialization() hooked → NoOp");
            }
            // NavRecord.Dispose(bool) and NavRecord.IsGlobalTriggerImplemented are Cecil-owned
            // (see NclCecilRewrite.cs).

            // NOTE: NavRecord.get_ALReadPermission / get_ALWritePermission / get_ALReadConsistency
            // and ALAddLoadFields / ALSetBaseLoadFields / AddLoadFields hooks are disabled —
            // JmpHook on these R2R-compiled NavRecord methods causes SIGSEGV in
            // ExecutionListener..cctor() during first test run. Needs Cecil-rewrite workaround.
            // TODO: re-enable when a Cecil-safe approach is found.

            // NavRecord.InsertAsync(DataError, bool, bool, bool) — full body NREs through
            // NavCurrentThread.ResolveAppGroup / metaTable.IsEventSubscribed / DataModificationListener
            // before reaching the storage layer. Replace the whole body with a minimal call that
            // delegates straight to recordImplementation.InsertRecordAsync — which goes through our
            // already-hooked TempTableDataProvider DataAccessSource. Skips trigger/event dispatch
            // (W-8 will reintroduce that on top of the temp store).
            _fNavRecordRecordImplementation = navRecordType.GetField("recordImplementation",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var recImplTypeForWrites = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.RecordImplementation");
            if (recImplTypeForWrites != null)
            {
                _mRecordImplementationInsertRecordAsync = recImplTypeForWrites.GetMethod(
                    "InsertRecordAsync",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                _mRecordImplementationModifyRecordAsync = recImplTypeForWrites.GetMethod(
                    "ModifyRecordAsync",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                _mRecordImplementationDeleteRecordAsync = recImplTypeForWrites.GetMethod(
                    "DeleteRecordAsync",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                _mRecordImplementationRenameRecordAsync = recImplTypeForWrites.GetMethod(
                    "RenameRecordAsync",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            }
            _mNavRecordCloneRecord = navRecordType.GetMethod("CloneRecord",
                BindingFlags.Public | BindingFlags.Instance);
            // W-8a PR1: bypass-drain for InsertAsync. The real Ncl body
            // (NavRecord-275.cs:2832-2906) now runs end-to-end:
            //   - DataModificationListener.Instance: null-checked → no-op when null
            //   - WriteEventRaised: gated on Session.IsEventSessionRecorderEnabled (false)
            //   - NavCurrentThread.ResolveAppGroup: falls back to NavAppGroup.BaseGroup
            //   - NavGlobalTriggers.InsertAsync: short-circuits via IsGlobalTriggerImplemented hook
            //   - metaTable.IsInsertTriggerDefined: reflects on Record{id}.OnInsert override
            //     (works after RecordPatches.NCLMetaApplicationObject_get_ApplicationObjectClrType
            //     hierarchy-walk fix to discover inherited `objectId` non-public field)
            //   - metaTable.IsEventSubscribed: false until subscribers registered (PR2 work)
            //   - ParentCompany.TrackChanges: null-tolerant, real body handles
            //
            // The InsertAsync hook is intentionally NOT installed below so the real
            // Ncl trigger-dispatch path runs. NavRecord_InsertAsync replacement body
            // is left in place for reference / quick re-enable if needed.
            //
            // Modify/Delete/Rename remain bypassed for PR1 scope; W-8 follow-on PR
            // will drain those in lockstep once the Insert path is validated.

            // AutoIncrement: ALInsertAsync(DataError,bool,bool) is an async ValueTask<bool> method.
            // JmpHook is unreliable on async ValueTask under R2R — hooking it causes SIGSEGV.
            // The Cecil-rewrite workaround is needed; skipping this hook for now.
            _mNavRecordALInsertAsync3 = navRecordType.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "ALInsertAsync"
                    && m.GetParameters() is { Length: 3 } ps3
                    && ps3[0].ParameterType.Name == "DataError");
            // NOTE: hook intentionally NOT installed — see above comment.

            // ALInit: hook ALInit() to also zero PK fields (BC v28+ behavior).
            _mNavRecordALInit = navRecordType.GetMethod("ALInit",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
            if (_mNavRecordALInit != null)
            {
                // NOTE: temporarily disabled — investigating intermittent SIGSEGV in bucket-2.
                // Hook(_mNavRecordALInit, nameof(NavRecord_ALInit), "NavRecord.ALInit()");
            }
            // W-8a PR3: bypass-drain for ModifyAsync. Safe to land now that f8367536's
            // bounded-depth recursion guard (NavMethodScope depth counter, 500 frames) catches
            // Codeunit108002.Modify_WithRecursiveTrigger_DoesNotStackOverflow before the
            // process stack-overflows.
            //   - IsModifyTriggerDefined powered by the same inherited-objectId field-walk fix
            //     as Delete/Rename.
            //   - TrackChanges no-ops cleanly in headless mode.
            //
            // The ModifyAsync hook is intentionally NOT installed. NavRecord_ModifyAsync
            // replacement body is left in place for reference.
            var modifyAsync4 = navRecordType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "ModifyAsync" && m.GetParameters().Length == 4);

            // W-8a PR2: bypass-drain for DeleteAsync. Same rationale as ModifyAsync above:
            //   - IsDeleteTriggerDefined powered by inherited-objectId field-walk fix.
            //   - TrackChanges no-ops cleanly.
            //
            // The DeleteAsync hook is intentionally NOT installed. NavRecord_DeleteAsync
            // replacement body is left in place for reference.
            var deleteAsync4 = navRecordType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "DeleteAsync" && m.GetParameters().Length == 4);

            // W-8a PR2: bypass-drain for RenameAsync. Signature differs from the others:
            //   NavRecord.RenameAsync(DataError, bool runApplicationTrigger, bool runGlobalTrigger, NavValue[])
            //   - IsRenameTriggerDefined powered by inherited-objectId field-walk fix.
            //   - TrackChanges no-ops cleanly.
            //
            // The RenameAsync hook is intentionally NOT installed. NavRecord_RenameAsync
            // replacement body is left in place for reference.
            var renameAsync4 = navRecordType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "RenameAsync" && m.GetParameters().Length == 4);
        }
        // RecordLink.MoveLinksAsync(NavRecord,NavRecord) and NavRecord.UpdateReferencesOnRenameAsync
        // are Cecil-owned (see NclCecilRewrite.cs).

        // NavManagementTasks.CopyCompany, NCLMetaApplicationObject.CheckApplicationObjectIsValid,
        // and NCLMetaApplicationObject.get_ApplicationObjectClrType are all Cecil-owned (see
        // NclCecilRewrite.cs).

        // RecordImplementation.VerifyPermissions, .VerifySecurityFiltersOnRecordAsync,
        // .VerifySecurityFiltersAsync, and .get_IsOpen are Cecil-owned (see NclCecilRewrite.cs).
        // .InternalFindRecordWithoutCheckingValuesAsync is also Cecil-owned there, but it calls
        // straight through to RecordImpl_InternalFindRecordWithoutCheckingValuesAsync below, so
        // the field/method lookups that helper depends on still need to be resolved here.
        var recImplType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.RecordImplementation");
        if (recImplType != null)
        {
            _fRecordImplementationDataAccess = recImplType.GetField("dataAccess",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _fRecordImplementationMutableRecordBuffer = recImplType.GetField("mutableRecordBuffer",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _fRecordImplementationMetaTable = recImplType.GetField("metaTable",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var dataAccessType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.DataAccess");
            if (dataAccessType != null)
            {
                _mDataAccessTryGetByPrimaryKeyAsync = dataAccessType.GetMethod("TryGetByPrimaryKeyAsync",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            var mrbResultType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.MutableRecordBufferResult`1")
                ?.MakeGenericType(typeof(bool));
            if (mrbResultType != null)
            {
                _pMrbResultResult = mrbResultType.GetProperty("Result");
                _pMrbResultRecordBuffer = mrbResultType.GetProperty("RecordBuffer");
            }
        }

        // NavServerEventSource.WritePermissionUncheckedEvent — telemetry event called from
        // RecordImplementation.InternalFindRecordWithoutCheckingValuesAsync; the property
        // get_NavServerTracingEvents NREs because the singleton EventSource is uninitialized in
        // headless mode. No-op the public method, AND ensure NavServerEventSource.Log returns a
        // non-null instance so the call-site doesn't NRE on virtual dispatch.
        var navServerEventSourceType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavServerEventSource");
        if (navServerEventSourceType != null)
        {
            // Pre-build an uninitialised NavServerEventSource singleton (cached in static field).
            _skeletonNavServerEventSource = RuntimeHelpers.GetUninitializedObject(navServerEventSourceType);

            // GetUninitializedObject skips field initializers, so the instance's
            // `private readonly object mutex = new object();` never ran (mutex == null).
            // Every EventViewer-channel property getter is `lock(mutex) { ... }`;
            // lock(null) -> Monitor.ReliableEnter -> ArgumentNullException. Because
            // TrappableOperationExecutor.HandleError logs the caught exception via
            // this singleton BEFORE mapping/rethrowing it, that ArgumentNullException
            // REPLACED the real error on every trappable operation. Poke a real mutex
            // so the logging path just works (and no-ops on the empty sinks).
            var mutexField = navServerEventSourceType.GetField("mutex",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (mutexField != null)
                AlRunner.Infrastructure.FieldPoke.SetInstance(mutexField, _skeletonNavServerEventSource, new object());
            else
                Console.Error.WriteLine("[BcRuntime] WARNING: NavServerEventSource.mutex field not found — trappable-error logging may mask real exceptions");

            // NavServerEventSource.get_Log and .WritePermissionUncheckedEvent are Cecil-owned
            // (see NclCecilRewrite.cs) — get_Log's Cecil body reads _skeletonNavServerEventSource
            // populated above.
        }

        // NavSession.get_SortingProperties, SequentialUuidCreator.NativeMethods.NewSequentialId,
        // and TempTableStatistics.ReportIncrementChange are all Cecil-owned (see
        // NclCecilRewrite.cs).

        // NavTextBuilder.ALInsert(DataError, int, string) — BC v28+: position 0 = prepend
        // (equivalent to position 1). BC v27 throws for position=0.
        // NOTE: JmpHook is intermittently unreliable for this method under R2R.
        // Disabled until a Cecil-rewrite workaround is in place.
        var navTextBuilderType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavTextBuilder");
        if (navTextBuilderType != null)
        {
            _mNavTextBuilderALInsert = navTextBuilderType.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "ALInsert" && m.GetParameters().Length == 3);
            // Hook intentionally NOT installed — see above comment.
        }
        // ── END RECORD PATCHES ────────────────────────────────────────────────────────
    }

    /// <summary>
    /// BC's own record-not-found exception for a failed primary-key/SystemId lookup, built
    /// through RecordImplementationHelper so the AL-visible message ("The &lt;table&gt; does not
    /// exist. Identification fields and values: ...") is BC's, not an approximation of it.
    /// The original body raised this too; the only thing this replacement still skips is the
    /// WritePermissionUncheckedEvent telemetry above it, which reads
    /// Session.CurrentMethodScope.ApplicationObject and NREs on the skeleton root scope.
    /// </summary>
    private static Exception BuildRecordNotFoundException(object self, object request)
    {
        var nclAsm = typeof(Microsoft.Dynamics.Nav.Runtime.NavRecord).Assembly;
        var helper = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.RecordImplementationHelper");
        var metaTable = _fRecordImplementationMetaTable?.GetValue(self);
        if (helper == null || metaTable == null)
            throw new InvalidOperationException(
                "RecordImplementationHelper / RecordImplementation.metaTable not resolvable — a " +
                "failed Record.Get cannot raise BC's record-not-found error and would silently " +
                "succeed instead.");

        // RecordIdCacheRequest.RecordId / SystemIdCacheRequest.SystemId — pick whichever this
        // request carries, mirroring the original body's own two-way branch.
        foreach (var (propName, argTypeName) in new[]
                 {
                     ("RecordId", "Microsoft.Dynamics.Nav.Runtime.NavRecordId"),
                     ("SystemId", "Microsoft.Dynamics.Nav.Runtime.NavGuid"),
                 })
        {
            var key = request.GetType().GetProperty(propName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(request);
            if (key == null) continue;
            var argType = nclAsm.GetType(argTypeName);
            var factory = helper.GetMethod("GetRecordNotFoundException",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, new[] { metaTable.GetType(), argType! }, modifiers: null);
            if (factory?.Invoke(null, new[] { metaTable, key }) is Exception ex)
                return AlRunner.Infrastructure.MissingTestDataDiagnosis.TagTable(ex, metaTable);
        }

        throw new InvalidOperationException(
            $"Record not found, but the request type {request.GetType().Name} carries neither a " +
            "RecordId nor a SystemId, so BC's record-not-found error cannot be built. Refusing to " +
            "report the lookup as successful.");
    }

    /// <summary>
    /// Replacement for RecordImplementation.InternalFindRecordWithoutCheckingValuesAsync —
    /// thin passthrough that hits dataAccess.TryGetByPrimaryKeyAsync and bypasses the original
    /// body's permission-event/diagnostic args evaluation, which NREs through
    /// Session.CurrentMethodScope.ApplicationObject (null on the skeleton root scope) when the
    /// requested record is not found.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static System.Threading.Tasks.ValueTask<bool> RecordImpl_InternalFindRecordWithoutCheckingValuesAsync(
        object self,
        Microsoft.Dynamics.Nav.Types.DataError errorLevel,
        object request,
        bool useRecord,
        bool calcAutoCalcFields)
    {
        try
        {
            var dataAccess = _fRecordImplementationDataAccess?.GetValue(self);
            if (dataAccess == null || _mDataAccessTryGetByPrimaryKeyAsync == null)
                return new System.Threading.Tasks.ValueTask<bool>(false);
            var taskObj = _mDataAccessTryGetByPrimaryKeyAsync.Invoke(dataAccess, new[] { request });
            if (taskObj == null) return new System.Threading.Tasks.ValueTask<bool>(false);

            // taskObj is ValueTask<MutableRecordBufferResult<bool>> — block via .AsTask().Result.
            var asTaskMi = taskObj.GetType().GetMethod("AsTask");
            var asTask = asTaskMi?.Invoke(taskObj, null) as System.Threading.Tasks.Task;
            asTask?.Wait();
            var resultObj = asTask?.GetType().GetProperty("Result")?.GetValue(asTask);
            if (resultObj == null) return new System.Threading.Tasks.ValueTask<bool>(false);

            bool found = (bool)(_pMrbResultResult!.GetValue(resultObj) ?? false);
            if (found && useRecord)
            {
                var recBuffer = _pMrbResultRecordBuffer?.GetValue(resultObj);
                _fRecordImplementationMutableRecordBuffer?.SetValue(self, recBuffer);
            }
            if (found) return new System.Threading.Tasks.ValueTask<bool>(true);

            // Not found. BC's own body decides the failure mode RIGHT HERE from errorLevel —
            // nothing downstream re-checks — so returning false unconditionally silently turned
            // every raising Get into a succeeding one. AL picks the mode at the call site:
            // `if Rec.Get(x) then` compiles to TrapError and wants false; a bare `Rec.Get(x);`
            // compiles to ThrowError and must raise, otherwise the caller keeps whatever the
            // record held before and every later assertion tests something that never happened.
            if (errorLevel == Microsoft.Dynamics.Nav.Types.DataError.TrapError)
                return new System.Threading.Tasks.ValueTask<bool>(false);

            throw BuildRecordNotFoundException(self, request);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(tie.InnerException);
            return default;
        }
    }


    /// <summary>
    /// Replacement for NavRecord.InsertAsync(DataError, bool, bool, bool).
    /// Bypasses all the trigger/event/extension dispatch that NREs on a skeleton session
    /// (NavCurrentThread.ResolveAppGroup, DataModificationListener, etc.) and delegates straight
    /// to recordImplementation.InsertRecordAsync, which goes through our hooked
    /// TempTableDataProvider DataAccessSource. W-8 will layer trigger dispatch back on top of
    /// this once permanent-table semantics are wired up.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static System.Threading.Tasks.ValueTask<bool> NavRecord_InsertAsync(
        Microsoft.Dynamics.Nav.Runtime.NavRecord self,
        Microsoft.Dynamics.Nav.Types.DataError errorLevel,
        bool runApplicationTrigger,
        bool runGlobalTrigger,
        bool insertWithSystemId)
    {
        try
        {
            var recImpl = _fNavRecordRecordImplementation?.GetValue(self);
            if (recImpl == null || _mRecordImplementationInsertRecordAsync == null)
                return new System.Threading.Tasks.ValueTask<bool>(false);
            var result = _mRecordImplementationInsertRecordAsync.Invoke(recImpl, new object?[] { errorLevel });
            if (result is System.Threading.Tasks.ValueTask<bool> vt) return vt;
            return new System.Threading.Tasks.ValueTask<bool>(true);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(tie.InnerException);
            return default; // unreachable
        }
    }

    /// <summary>
    /// Replacement for NavRecord.ModifyAsync(DataError, bool, bool, bool).
    /// Same bypass pattern as InsertAsync — skips trigger/event dispatch that NREs on skeleton
    /// session and delegates to RecordImplementation.ModifyRecordAsync directly.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static System.Threading.Tasks.ValueTask<bool> NavRecord_ModifyAsync(
        Microsoft.Dynamics.Nav.Runtime.NavRecord self,
        Microsoft.Dynamics.Nav.Types.DataError errorLevel,
        bool runApplicationTrigger,
        bool runGlobalTrigger,
        bool isBulkModify)
    {
        try
        {
            var recImpl = _fNavRecordRecordImplementation?.GetValue(self);
            if (recImpl == null || _mRecordImplementationModifyRecordAsync == null)
                return new System.Threading.Tasks.ValueTask<bool>(false);
            var result = _mRecordImplementationModifyRecordAsync.Invoke(recImpl, new object?[] { errorLevel });
            if (result is System.Threading.Tasks.ValueTask<bool> vt) return vt;
            return new System.Threading.Tasks.ValueTask<bool>(true);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(tie.InnerException);
            return default;
        }
    }

    /// <summary>
    /// Replacement for NavRecord.DeleteAsync(DataError, bool, bool, bool).
    /// Same bypass pattern as InsertAsync — skips trigger/event dispatch that NREs on skeleton
    /// session and delegates to RecordImplementation.DeleteRecordAsync directly.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static System.Threading.Tasks.ValueTask<bool> NavRecord_DeleteAsync(
        Microsoft.Dynamics.Nav.Runtime.NavRecord self,
        Microsoft.Dynamics.Nav.Types.DataError errorLevel,
        bool runApplicationTrigger,
        bool isCalledFromUI,
        bool isBulkDelete)
    {
        try
        {
            var recImpl = _fNavRecordRecordImplementation?.GetValue(self);
            if (recImpl == null || _mRecordImplementationDeleteRecordAsync == null)
                return new System.Threading.Tasks.ValueTask<bool>(false);
            var result = _mRecordImplementationDeleteRecordAsync.Invoke(recImpl, new object?[] { errorLevel });
            if (result is System.Threading.Tasks.ValueTask<bool> vt) return vt;
            return new System.Threading.Tasks.ValueTask<bool>(true);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(tie.InnerException);
            return default;
        }
    }

    /// <summary>
    /// Replacement for NavRecord.RenameAsync(DataError, bool, bool, NavValue[]).
    /// Bypasses trigger/event dispatch. Clones self, sets new PK field values on the clone
    /// using NCLMetaField directly (avoids GetFieldByNo), then calls
    /// RecordImplementation.RenameRecordAsync(errorLevel, renamedRecord).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static System.Threading.Tasks.ValueTask<bool> NavRecord_RenameAsync(
        Microsoft.Dynamics.Nav.Runtime.NavRecord self,
        Microsoft.Dynamics.Nav.Types.DataError errorLevel,
        bool runApplicationTrigger,
        bool runGlobalTrigger,
        Microsoft.Dynamics.Nav.Runtime.NavValue[] values)
    {
        try
        {
            var recImpl = _fNavRecordRecordImplementation?.GetValue(self);
            if (recImpl == null || _mRecordImplementationRenameRecordAsync == null || _mNavRecordCloneRecord == null)
                return new System.Threading.Tasks.ValueTask<bool>(false);

            values ??= Array.Empty<Microsoft.Dynamics.Nav.Runtime.NavValue>();
            var key = self.MetaTable.GetKeyByIndex(0);
            if (values.Length < key.KeyFieldCount)
                return new System.Threading.Tasks.ValueTask<bool>(false);

            var newRecord = (Microsoft.Dynamics.Nav.Runtime.NavRecord)_mNavRecordCloneRecord.Invoke(
                self, new object[] { self, false, true })!;
            for (int i = 0; i < key.KeyFieldCount; i++)
            {
                var field = key.GetKeyFieldByIndex(i);
                newRecord.SetFieldValue(field, values[i]);
            }
            var result = _mRecordImplementationRenameRecordAsync.Invoke(recImpl, new object[] { errorLevel, newRecord });
            if (result is System.Threading.Tasks.ValueTask<bool> vt) return vt;
            return new System.Threading.Tasks.ValueTask<bool>(true);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(tie.InnerException);
            return default;
        }
    }

    /// <summary>No-op replacement for NavRecord.BeginInitialization and EndInitialization
    /// — these dereference Session.MetadataProvider (null on the skeleton), causing NREs
    /// identical to the NavXmlPort/NavReport.BeginInitialization clusters.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavRecord_BeginEndInitialization(object self)
    {
    }

    /// <summary>
    /// Register a tableId + fieldNo pair as an AutoIncrement-enabled field.
    /// Called by NclMetaTableBuilder after constructing an NCLMetaTable that has an
    /// AutoIncrement field, so <see cref="NavRecord_ALInsertAsync3"/> can assign the counter.
    /// </summary>
    public static void RegisterAutoIncrementField(int tableId, int fieldNo)
        => _aiFieldIds[tableId] = fieldNo;

    /// <summary>
    /// AutoIncrement assignment helper, called via Cecil-prepended IL at the start
    /// of NavRecord.ALInsertAsync(DataError, bool, bool). Pure side-effect:
    /// if the table has a registered AutoIncrement field and that field is currently
    /// zero/empty on `self`, advance the per-table counter and stamp the new value
    /// into the field. Any exception is swallowed — the real async body then runs
    /// unchanged; if AI couldn't be assigned, the duplicate-key check downstream is
    /// the observable failure mode, same as without this helper. The signature
    /// (NavRecord)→void is deliberately minimal so the Cecil prepend is a single
    /// `ldarg.0; call` pair with no stack-balance complications.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void AssignAutoIncrement(Microsoft.Dynamics.Nav.Runtime.NavRecord self)
    {
        try
        {
            if (self.IsTemporary || IsMetaTableTemporary(self.MetaTable))
                return;

            int tableId = self.MetaTable.TableId;
            if (_aiFieldIds.TryGetValue(tableId, out int aiFieldNo)
                && self.MetaTable.TryGetFieldByNo(aiFieldNo, out var aiField))
            {
                var currentVal = self.GetFieldValue(aiField);
                if (currentVal.IsZeroOrEmpty)
                {
                    long next = _aiCounters.AddOrUpdate(tableId, 1L, (_, v) => v + 1L);
                    var newVal = Microsoft.Dynamics.Nav.Runtime.NavValue
                        .CreateNavValueFromObject(aiField, (object)(int)next);
                    self.SetFieldValue(aiField, newVal);
                }
            }
        }
        catch { /* don't block insert if AI counter assignment fails */ }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsMetaTableTemporary(Microsoft.Dynamics.Nav.Runtime.NCLMetaTable metaTable)
    {
        try
        {
            var tableTypeObj = metaTable.GetType()
                .GetProperty("TableType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(metaTable);
            return string.Equals(tableTypeObj?.ToString(), "Temporary", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    // ── System fields (2000000001-2000000004) stamping helpers ──────────────────────
    // Called via Cecil-prepended IL at the start of NavRecord.ALInsertAsync and
    // ALModifyAsync. Both helpers are (NavRecord)→void so the Cecil prepend is the
    // same minimal `ldarg.0; call` pair as AssignAutoIncrement above.

    private static readonly System.Guid _sessionUserGuid = System.Guid.NewGuid();
    private static System.Guid GetOrCreateSessionUserGuid() => _sessionUserGuid;

    private static void TryStampDateTime(
        Microsoft.Dynamics.Nav.Runtime.NavRecord self,
        Microsoft.Dynamics.Nav.Runtime.NCLMetaTable meta,
        int fieldNo,
        System.DateTime value)
    {
        try
        {
            if (meta.TryGetFieldByNo(fieldNo, out var field))
            {
                var nv = Microsoft.Dynamics.Nav.Runtime.NavValue
                    .CreateNavValueFromObject(field, (object)value);
                self.SetFieldValue(field, nv);
            }
        }
        catch { /* skip on any per-field failure */ }
    }

    private static void TryStampGuid(
        Microsoft.Dynamics.Nav.Runtime.NavRecord self,
        Microsoft.Dynamics.Nav.Runtime.NCLMetaTable meta,
        int fieldNo,
        System.Guid value)
    {
        try
        {
            if (meta.TryGetFieldByNo(fieldNo, out var field))
            {
                var nv = Microsoft.Dynamics.Nav.Runtime.NavValue
                    .CreateNavValueFromObject(field, (object)value);
                self.SetFieldValue(field, nv);
            }
        }
        catch { /* skip on any per-field failure */ }
    }

    /// <summary>
    /// Stamps SystemCreatedAt/By/ModifiedAt/By on Insert. Called via Cecil prepend
    /// on NavRecord.ALInsertAsync(DataError,bool,bool).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void StampSystemFieldsOnInsert(Microsoft.Dynamics.Nav.Runtime.NavRecord self)
    {
        try
        {
            var meta = self.MetaTable;
            var nowUtc = System.DateTime.UtcNow;
            var sessionUser = GetOrCreateSessionUserGuid();

            TryStampDateTime(self, meta, 2000000001, nowUtc);    // SystemCreatedAt
            TryStampGuid    (self, meta, 2000000002, sessionUser); // SystemCreatedBy
            TryStampDateTime(self, meta, 2000000003, nowUtc);    // SystemModifiedAt
            TryStampGuid    (self, meta, 2000000004, sessionUser); // SystemModifiedBy
        }
        catch { /* never block insert */ }
    }

    /// <summary>
    /// Stamps only SystemModifiedAt/By on Modify. NEVER touches SystemCreatedAt/By.
    /// Called via Cecil prepend on NavRecord.ALModifyAsync.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void StampSystemFieldsOnModify(Microsoft.Dynamics.Nav.Runtime.NavRecord self)
    {
        try
        {
            var meta = self.MetaTable;
            var nowUtc = System.DateTime.UtcNow;
            var sessionUser = GetOrCreateSessionUserGuid();

            TryStampDateTime(self, meta, 2000000003, nowUtc);    // SystemModifiedAt
            TryStampGuid    (self, meta, 2000000004, sessionUser); // SystemModifiedBy
        }
        catch { /* never block modify */ }
    }

    /// <summary>
    /// Replacement for NavRecord.ALInsertAsync(DataError, bool, bool).
    /// Assigns the next AutoIncrement counter value to the AI field when it is zero,
    /// then calls the real ALInsertAsync body via MethodInfo.Invoke (bypasses the hook).
    /// NOTE: this JmpHook-mode replacement is intentionally NOT installed — see notes
    /// at the hook discovery site. AutoIncrement is delivered via Cecil prepend on
    /// NavRecord.ALInsertAsync(DataError,bool,bool) calling AssignAutoIncrement above.
    /// Kept as reference for the equivalence claim.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static System.Threading.Tasks.ValueTask<bool> NavRecord_ALInsertAsync3(
        Microsoft.Dynamics.Nav.Runtime.NavRecord self,
        Microsoft.Dynamics.Nav.Types.DataError errorLevel,
        bool runApplicationTrigger,
        bool insertWithSystemId)
    {
        try
        {
            if (self.IsTemporary || IsMetaTableTemporary(self.MetaTable))
                goto InvokeRealBody;

            int tableId = self.MetaTable.TableId;
            if (_aiFieldIds.TryGetValue(tableId, out int aiFieldNo)
                && self.MetaTable.TryGetFieldByNo(aiFieldNo, out var aiField))
            {
                var currentVal = self.GetFieldValue(aiField);
                if (currentVal.IsZeroOrEmpty)
                {
                    long next = _aiCounters.AddOrUpdate(tableId, 1L, (_, v) => v + 1L);
                    var newVal = Microsoft.Dynamics.Nav.Runtime.NavValue
                        .CreateNavValueFromObject(aiField, (object)(int)next);
                    self.SetFieldValue(aiField, newVal);
                }
            }
        }
        catch { /* don't block insert if AI counter assignment fails */ }

    InvokeRealBody:
        try
        {
            var result = _mNavRecordALInsertAsync3!.Invoke(self,
                new object[] { errorLevel, runApplicationTrigger, insertWithSystemId });
            if (result is System.Threading.Tasks.ValueTask<bool> vt) return vt;
            if (result is System.Threading.Tasks.Task<bool> t)
                return new System.Threading.Tasks.ValueTask<bool>(t);
            return new System.Threading.Tasks.ValueTask<bool>(true);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(tie.InnerException);
            return default;
        }
    }

    /// <summary>
    /// Replacement for NavRecord.ALInit().
    /// Calls the real ALInit() body (which resets non-PK fields) then also zeroes PK
    /// fields to match BC v28+ behavior where Init() resets all fields including PK.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavRecord_ALInit(Microsoft.Dynamics.Nav.Runtime.NavRecord self)
    {
        try
        {
            _mNavRecordALInit!.Invoke(self, null);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(tie.InnerException);
        }
        // Zero PK fields — BC v28+ resets them too on Init()
        try
        {
            var key = self.MetaTable.GetKeyByIndex(0);
            for (int i = 0; i < key.KeyFieldCount; i++)
            {
                var field = key.GetKeyFieldByIndex(i);
                self.SetFieldValue(field, field.EmptyValue);
            }
        }
        catch { /* don't fail Init() if PK zeroing encounters issues */ }
    }

    /// <summary>
    /// Replacement for NavTextBuilder.ALInsert(DataError, int, string).
    /// BC v28+: position 0 means prepend (equivalent to position 1). BC v27 throws.
    /// Convert position 0 → 1, then call the real body via MethodInfo.Invoke.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool NavTextBuilder_ALInsert(
        object self,
        Microsoft.Dynamics.Nav.Types.DataError errorLevel,
        int position,
        string str)
    {
        if (position == 0) position = 1;
        try
        {
            var result = _mNavTextBuilderALInsert!.Invoke(self,
                new object[] { errorLevel, position, str });
            return result is bool b && b;
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(tie.InnerException);
            return false;
        }
    }
}
