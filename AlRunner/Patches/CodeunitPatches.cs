// CodeunitPatches — replacements for NavCodeunit.DoRunAsync and NavCodeunitHandle.CreateTarget.
//
// DoRunAsync normally goes through DiagnosticsResolver and the metadata layer; we bypass
// both and call the AL-emitted OnRun directly via reflection.
//
// CreateTarget normally looks up the codeunit class via NavGlobal.NCLMetadata; we replace
// it with an assembly-scan for `Codeunit{ID}` in the loaded test assembly.
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner;

public static partial class BcRuntime
{
    // Cache: codeunit ID → generated codeunit Type (cleared per-test-assembly via SetTestAssembly).
    private static readonly ConcurrentDictionary<int, Type?> _codeunitTypeCache = new();

    // Cache: codeunit ID → the single shared instance, for SingleInstance=true codeunits only.
    // Real BC gives a SingleInstance codeunit exactly one instance per session, so instance
    // fields persist across every call/reference within a test. Cleared at the per-test
    // boundary by ResetSingleInstanceCache() (called from RecordPatches.ResetPerTestState(),
    // itself invoked at both codeunit- and test-level isolation boundaries in TestExecutor) —
    // otherwise SingleInstance state would leak from one test into the next, which real BC's
    // per-test rollback does not allow.
    private static readonly ConcurrentDictionary<int, Microsoft.Dynamics.Nav.Runtime.NavCodeunit> _singleInstanceCache = new();

    // Session-rooted handles that exist ONLY to hold a reference on each cached SingleInstance
    // codeunit. BC refcounts a codeunit instance through the handles that point at it:
    // TreeObjectReferenceHandler.SetReferenceTarget/Dispose call
    // InternalRemoveReferenceDisposeIfLast, which DISPOSES the instance the moment the count
    // reaches zero. The dictionary above is a plain C# reference and is invisible to that
    // count, so as soon as the last AL variable naming the codeunit went out of scope, BC
    // disposed the instance we were still caching. A disposed tree is not an error the next
    // caller sees — NavApplicationObjectBaseHandle.Target returns NULL for a disposed tree
    // instead of rebuilding — so every global record handle on the codeunit silently read back
    // null and the next access NRE'd with no message inside BC (measured: Base App codeunit
    // 347 "Auto Format".GetGLSetup; `tree=DISPOSED` logged immediately before the NRE).
    // Real BC does not hit zero because the session itself holds a SingleInstance codeunit;
    // this handle is that reference, and it is parented on the session so it lives exactly as
    // long as the cache entry does.
    private static readonly ConcurrentDictionary<int, Microsoft.Dynamics.Nav.Runtime.NavCodeunitHandle> _singleInstanceKeepAlive = new();

    // Every AL variable handle that has already resolved a SingleInstance codeunit through
    // NavCodeunitHandle_CreateTarget, held WEAKLY so a handle whose AL scope has gone can
    // still be collected.
    //
    // _singleInstanceCache above is NOT the only place recording "which object is the
    // instance of codeunit N". NavApplicationObjectBaseHandle<T>.get_Target is unmodified
    // BC code and reads, in IL:
    //
    //     target = Tree.GetReferenceTarget();
    //     if (target == null && !Tree.IsDisposed) {
    //         target = CreateTarget();            // our replacement, consults the cache
    //         Tree.SetReferenceTarget(target);    // the SECOND copy of the answer
    //     }
    //     return target;
    //
    // so a handle calls CreateTarget exactly ONCE and then answers from its own tree for as
    // long as the AL variable holding it lives. Clearing only the cache left those handles
    // returning the instance the reset had dropped, while any handle resolving afterwards
    // got a new one — two live "single" instances, and AL writing through one could not be
    // read through the other. That is issue #2143: it cost 15 corpus tests under #2144's
    // per-test reset (where the test codeunit instance, and therefore its global Codeunit
    // variables' handles, is shared by every test in the codeunit), and #2161 moved the
    // reset rather than fixing it. Registering here and invalidating below keeps the two
    // records in step whenever the reset fires, not only where it happens to fire today.
    private static readonly List<WeakReference<Microsoft.Dynamics.Nav.Runtime.NavCodeunitHandle>>
        _singleInstanceBoundHandles = new();
    private static readonly object _singleInstanceBoundHandlesLock = new();

    /// <summary>
    /// Remember that <paramref name="handle"/> is about to cache a SingleInstance codeunit
    /// instance of its own, so <see cref="ResetSingleInstanceCache"/> can make it re-resolve.
    /// Called from NavCodeunitHandle_CreateTarget on every path that hands a SingleInstance
    /// instance back — including the cache-hit path, because the handle receiving the hit is
    /// a DIFFERENT handle each time and each one caches the answer independently.
    /// </summary>
    private static void TrackSingleInstanceBinding(Microsoft.Dynamics.Nav.Runtime.NavCodeunitHandle handle)
    {
        lock (_singleInstanceBoundHandlesLock)
            _singleInstanceBoundHandles.Add(
                new WeakReference<Microsoft.Dynamics.Nav.Runtime.NavCodeunitHandle>(handle));
    }

    /// <summary>
    /// Drop every cached SingleInstance codeunit instance. Must run at the per-test-isolation
    /// boundary (see RecordPatches.ResetPerTestState) so SingleInstance field state does not
    /// leak across tests, mirroring real BC's per-test transaction rollback.
    /// </summary>
    public static void ResetSingleInstanceCache()
    {
        // Invalidate the per-handle copies FIRST — see _singleInstanceBoundHandles. Doing it
        // before the dictionaries are cleared matters only for readability; what matters for
        // correctness is that no handle is left holding an instance the cache no longer has.
        InvalidateBoundSingleInstanceHandles();
        // Release the keep-alive references first: dropping them is what lets BC's refcount
        // fall to zero and dispose the instances, which is the per-test cleanup real BC does.
        _singleInstanceKeepAlive.Clear();
        _singleInstanceCache.Clear();
    }

    /// <summary>
    /// Clear the cached target of every handle that resolved a SingleInstance codeunit, so
    /// its next <c>Target</c> read goes back through CreateTarget and picks up the instance
    /// everyone else is using.
    ///
    /// NavCodeunitHandle.ClearReference() is BC's own public API for exactly this — it is
    /// <c>set_Target(null)</c>, i.e. <c>Tree.SetReferenceTarget(null)</c>, which is the only
    /// state get_Target consults before rebuilding. It also drops the handle's reference on
    /// the instance, but that cannot dispose the instance here: the session-rooted keep-alive
    /// handle (see _singleInstanceKeepAlive) is still holding one at this point, so the
    /// refcount cannot reach zero while this loop runs.
    /// </summary>
    private static void InvalidateBoundSingleInstanceHandles()
    {
        lock (_singleInstanceBoundHandlesLock)
        {
            foreach (var weak in _singleInstanceBoundHandles)
            {
                if (!weak.TryGetTarget(out var handle))
                    continue;   // the AL scope holding it is gone; nothing can read it again
                // A disposed handle's tree already refuses to rebuild (get_Target returns
                // whatever GetReferenceTarget gives and skips CreateTarget when IsDisposed),
                // and touching it would only risk an ObjectDisposedException for no gain.
                if (handle.IsDisposed || !handle.HasTarget)
                    continue;
                handle.ClearReference();
            }
            _singleInstanceBoundHandles.Clear();
        }
    }

    // Fallback cache: report ID → skeleton NCLMetaReport built via CreateEmptyNCLMetaReport
    // when NavGlobal.NCLMetadata.GetMetaReportById returns null or throws.
    private static readonly ConcurrentDictionary<int, object> _metaReportFallbackCache = new();

    /// <summary>
    /// Replacement for NavCodeunit.DoRunAsync(DataError, NavRecord).
    /// Bypasses DiagnosticsResolver.GetMostSpecificInstance(Session) which NREs on the skeleton
    /// by calling the concrete subclass's OnRun(INavRecordHandle) directly via reflection.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static System.Threading.Tasks.ValueTask<bool> NavCodeunit_DoRunAsync(
        Microsoft.Dynamics.Nav.Runtime.NavCodeunit self,
        Microsoft.Dynamics.Nav.Types.DataError errorLevel,
        Microsoft.Dynamics.Nav.Runtime.NavRecord? record)
    {
        // Same branch BC's own DoRunAsync takes: anything other than ThrowError is the guarded
        // form (the Boolean result is consumed), which needs its own isolated transaction and is
        // therefore refused while an uncommitted write is pending. See
        // ALDatabasePatches.ThrowIfWriteTransactionStarted.
        if (errorLevel != Microsoft.Dynamics.Nav.Types.DataError.ThrowError)
            ALDatabasePatches.ThrowIfWriteTransactionStarted();

        try
        {
            InvokeOnRun(self, record);
            return new System.Threading.Tasks.ValueTask<bool>(true);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(tie.InnerException);
            return default;
        }
    }

    /// <summary>
    /// Replacement for NavCodeunit.RunCodeunit(DataError, int, NavRecord).
    /// Builds a NavCodeunitHandle from the current session, resolves the concrete target via
    /// NavCodeunitHandle_CreateTarget, then invokes OnRun directly (same OnRun dispatch as
    /// NavCodeunit_DoRunAsync) without touching the runtime's inlined DoRunAsync body.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool NavCodeunit_RunCodeunit(
        Microsoft.Dynamics.Nav.Types.DataError errorLevel,
        int objectId,
        Microsoft.Dynamics.Nav.Runtime.NavRecord? record)
    {
        bool trap = errorLevel == Microsoft.Dynamics.Nav.Types.DataError.TrapError;

        // Deliberately OUTSIDE the `catch when (trap)` below, exactly as BC places
        // BeginTransactionWorldAndTransaction outside the try whose catch suppresses the
        // codeunit's own errors: a refusal must reach the AL caller as an error, never be
        // converted into a `false` return. See ALDatabasePatches.ThrowIfWriteTransactionStarted.
        if (errorLevel != Microsoft.Dynamics.Nav.Types.DataError.ThrowError)
            ALDatabasePatches.ThrowIfWriteTransactionStarted();

        try
        {
            var handle = CreateCodeunitHandle(objectId);
            var target = NavCodeunitHandle_CreateTarget(handle);
            InvokeOnRun(target, record);
            return true;
        }
        catch when (trap)
        {
            // TrapError contract for Codeunit.Run(...): swallow and return false.
            return false;
        }
    }

    private static Microsoft.Dynamics.Nav.Runtime.NavCodeunitHandle CreateCodeunitHandle(int objectId)
    {
        var handleType = typeof(Microsoft.Dynamics.Nav.Runtime.NavCodeunitHandle);
        var navCurrentThreadType = typeof(Microsoft.Dynamics.Nav.Runtime.NavCurrentThread);
        var sessionProp = navCurrentThreadType.GetProperty("Session",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var session = sessionProp?.GetValue(null) ?? _skeletonSession!;
        if (session == null)
            throw new InvalidOperationException("NavCurrentThread.Session is null and skeleton session is unavailable.");

        var ctor = handleType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(c =>
            {
                var ps = c.GetParameters();
                return ps.Length == 2 &&
                    ps[0].ParameterType.IsAssignableFrom(session.GetType()) &&
                    ps[1].ParameterType == typeof(int);
            });
        if (ctor == null)
            throw new InvalidOperationException("NavCodeunitHandle(NavSession, int) constructor not found.");

        return (Microsoft.Dynamics.Nav.Runtime.NavCodeunitHandle)ctor.Invoke(new[] { session, (object)objectId });
    }

    private static void InvokeOnRun(
        Microsoft.Dynamics.Nav.Runtime.NavCodeunit self,
        Microsoft.Dynamics.Nav.Runtime.NavRecord? record)
    {
        try
        {
            var concreteType = self.GetType();

            // The AL `trigger OnRun()` is compiled by BC's CodeAnalysis emitter into an
            // ASYNC method named `OnRunAsync` on the concrete codeunit class (and only the
            // dependency-compile path emits the async form; our test-bundle path emits a
            // synchronous `OnRun` override). `GetMethod("OnRun", ...)` returns the INHERITED
            // base `NavCodeunit.OnRun` (an empty no-op) when the concrete type only declares
            // `OnRunAsync` — so invoking it silently does nothing (the whole codeunit body
            // never runs). This is the bug that made `Codeunit.Run("Purch.-Post", …)` a no-op
            // on source-compiled Base App codeunits: posting produced no posted documents,
            // no ledger entries, and an empty `Last Posting No.`.
            //
            // Resolve the codeunit's OWN trigger entry: prefer a concrete `OnRunAsync`, then a
            // concrete `OnRun` override; both with the INavRecordHandle overload first, then the
            // parameterless form. Only fall back to an inherited `OnRun` if the concrete type
            // declares no trigger of its own (genuinely empty OnRun).
            var trigger = FindConcreteTrigger(concreteType, "OnRunAsync")
                       ?? FindConcreteTrigger(concreteType, "OnRun")
                       ?? concreteType.GetMethod("OnRun",
                              BindingFlags.NonPublic | BindingFlags.Instance, null,
                              new[] { typeof(Microsoft.Dynamics.Nav.Runtime.INavRecordHandle) }, null)
                       ?? concreteType.GetMethod("OnRun",
                              BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);

            if (trigger == null)
                return;

            object? result;
            if (trigger.GetParameters().Length == 1)
                result = trigger.Invoke(self, new object?[] { record });
            else
                result = trigger.Invoke(self, null);

            AwaitIfTask(result);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(tie.InnerException);
        }
    }

    /// <summary>
    /// Finds a codeunit-trigger method named <paramref name="name"/> that is DECLARED ON the
    /// concrete codeunit type itself (not inherited from <c>NavCodeunit</c>), preferring the
    /// <c>INavRecordHandle</c> overload over the parameterless form. Returns null if the
    /// concrete type does not declare such a trigger, so the caller can fall back.
    /// </summary>
    private static MethodInfo? FindConcreteTrigger(Type concreteType, string name)
    {
        var oneArg = concreteType.GetMethod(name,
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
            null, new[] { typeof(Microsoft.Dynamics.Nav.Runtime.INavRecordHandle) }, null);
        if (oneArg != null) return oneArg;
        return concreteType.GetMethod(name,
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
            null, Type.EmptyTypes, null);
    }

    /// <summary>
    /// If the trigger returned a Task / ValueTask (the async `OnRunAsync` emit), block on it so
    /// the synchronous <c>Codeunit.Run</c> contract holds and any AL error propagates here.
    /// </summary>
    private static void AwaitIfTask(object? result)
    {
        switch (result)
        {
            case null:
                return;
            case System.Threading.Tasks.Task t:
                t.GetAwaiter().GetResult();
                return;
            case System.Threading.Tasks.ValueTask vt:
                vt.GetAwaiter().GetResult();
                return;
            default:
                var rt = result.GetType();
                if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(System.Threading.Tasks.ValueTask<>))
                {
                    var asTask = rt.GetMethod("AsTask")!.Invoke(result, null) as System.Threading.Tasks.Task;
                    asTask?.GetAwaiter().GetResult();
                }
                return;
        }
    }

    /// <summary>
    /// <summary>
    /// Take a session-rooted reference on a cached SingleInstance codeunit so BC's own
    /// refcount can never fall to zero and dispose it — see _singleInstanceKeepAlive.
    /// NavCodeunitHandle(ITreeObject, NavCodeunit) assigns Target, which is what calls
    /// InternalAddReference; that is the whole point of using a real handle here rather than
    /// poking the counter.
    /// </summary>
    private static void KeepSingleInstanceAlive(int id, Microsoft.Dynamics.Nav.Runtime.NavCodeunit instance)
    {
        if (_singleInstanceKeepAlive.ContainsKey(id)) return;
        if (_skeletonSession is not Microsoft.Dynamics.Nav.Runtime.ITreeObject sessionParent)
            return;

        var ctor = typeof(Microsoft.Dynamics.Nav.Runtime.NavCodeunitHandle).GetConstructor(
            new[] { typeof(Microsoft.Dynamics.Nav.Runtime.ITreeObject),
                    typeof(Microsoft.Dynamics.Nav.Runtime.NavCodeunit) });
        if (ctor == null)
            throw new InvalidOperationException(
                "NavCodeunitHandle(ITreeObject, NavCodeunit) not found — a cached SingleInstance " +
                "codeunit cannot be kept alive, and BC would dispose it as soon as the last AL " +
                "variable naming it goes out of scope.");

        _singleInstanceKeepAlive[id] =
            (Microsoft.Dynamics.Nav.Runtime.NavCodeunitHandle)ctor.Invoke(new object[] { sessionParent, instance });
    }

    /// Replacement for NavCodeunitHandle.CreateTarget().
    /// Bypasses NavGlobal.NCLMetadata by looking up the compiled codeunit class directly
    /// from the loaded assembly and constructing it via the 1-arg ITreeObject ctor.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Microsoft.Dynamics.Nav.Runtime.NavCodeunit NavCodeunitHandle_CreateTarget(
        Microsoft.Dynamics.Nav.Runtime.NavCodeunitHandle self)
    {
        int id = self.ObjectId.ObjectNumber;

        // SingleInstance=true codeunits get exactly one instance per session — return the
        // cached one (built by an earlier caller, possibly with a different `self`/handle) so
        // its instance fields keep whatever state prior calls left in them. See the handle/
        // parent note below for why reusing the first caller's tree-parent is correct here.
        if (_singleInstanceCache.TryGetValue(id, out var cachedInstance))
        {
            // `self` is about to store this in its own tree (see TrackSingleInstanceBinding).
            TrackSingleInstanceBinding(self);
            return cachedInstance;
        }

        var codeunitType = _codeunitTypeCache.GetOrAdd(id, FindCodeunitType);
        if (codeunitType == null)
        {
            // System range (1-9999) and test-toolkit range (130000-139999) are
            // treated as no-ops when missing — the runner contract advertised
            // by tests/bucket-1/codeunit-runtime/128-codeunit-not-found is
            // that calling such a codeunit must succeed silently. Outside
            // those ranges we still throw with a helpful diagnostic.
            if ((id >= 1 && id <= 9999) || (id >= 130000 && id <= 139999))
                return new NoOpCodeunit(self, id);
            throw new InvalidOperationException(BuildMissingCodeunitMessage(id));
        }
        var ctor = codeunitType.GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Length == 1 &&
                typeof(Microsoft.Dynamics.Nav.Runtime.ITreeObject)
                    .IsAssignableFrom(c.GetParameters()[0].ParameterType));
        if (ctor == null)
            throw new InvalidOperationException(
                $"Codeunit{id} has no single-arg ITreeObject constructor");
        var instance = (Microsoft.Dynamics.Nav.Runtime.NavCodeunit)ctor.Invoke(new object[] { self });
        // Codeunit 151 = SystemInitializationImpl (SingleInstance=true, System Application).
        // Populate skeleton state (NOT a DLL-body rewrite — precompiled-dll-respect): set
        // initializationInProgress = true on the fresh instance so the unmodified
        // SystemInitialization.IsInProgress() returns true. See PrimeCodeunit151Instance.
        if (id == 151)
            AlRunner.BcRuntime.PrimeCodeunit151Instance(instance);

        // NavCodeunit.IsSingleInstance is a real, unmodified NCL property (base implementation
        // `return false`; BC's own emitter overrides it per-codeunit to a compile-time constant
        // from the AL `SingleInstance` property) — reading it off the freshly built instance is
        // faithful metadata, not a guess. Cache session-wide on first construction so every
        // later resolution of this codeunit id (from any handle) returns the SAME instance and
        // its instance fields persist across calls, matching real BC's one-instance-per-session
        // contract for SingleInstance=true codeunits.
        if (instance.IsSingleInstance)
        {
            // Rebuild it parented on the SESSION rather than on the caller's handle. The handle
            // belongs to whichever AL scope happened to resolve this codeunit first; when that
            // scope's tree is disposed the cascade takes every descendant with it, including
            // the codeunit we are about to cache for the rest of the test. A reference alone
            // does not save it — a cascading parent Dispose ignores refcounts — so the instance
            // has to live outside that subtree, which is where BC keeps a SingleInstance
            // codeunit anyway (NavCodeunitHandle.CreateTarget parents on base.Tree.Session).
            // Only SingleInstance codeunits are re-rooted: a per-call codeunit SHOULD die with
            // its scope, and session-rooting them all was measured to cost a test.
            if (_skeletonSession is Microsoft.Dynamics.Nav.Runtime.ITreeObject sessionParent)
                instance = (Microsoft.Dynamics.Nav.Runtime.NavCodeunit)ctor.Invoke(new object[] { sessionParent });
            if (id == 151)
                AlRunner.BcRuntime.PrimeCodeunit151Instance(instance);

            _singleInstanceCache[id] = instance;
            KeepSingleInstanceAlive(id, instance);
            TrackSingleInstanceBinding(self);
        }

        return instance;
    }

    // Cache: NavTestPage type (same type for all IDs; resolved once).
    private static Type? _navTestPageType;

    /// <summary>
    /// Replacement for NavTestPageHandle.CreateTarget().
    /// Creates a real NavTestPage via the internal 3-arg ctor (ITreeObject, ApplicationObjectId,
    /// ITestPage), passing a MockITestPage that satisfies all field/action/filter queries.
    /// Cecil IL rewrites in NclCecilRewrite neutralise the Open() path that would otherwise
    /// call TestExecution.ClientSession.CreatePage, making this mock sufficient.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object NavTestPageHandle_CreateTarget(object self)
    {
        var objIdProp = self.GetType().GetProperty("ObjectId",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var objId = objIdProp!.GetValue(self)!;

        // Resolve NavTestPage type from the Ncl assembly (same type regardless of page ID).
        _navTestPageType ??= self.GetType().Assembly
            .GetType("Microsoft.Dynamics.Nav.Runtime.NavTestPage")
            ?? AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("Microsoft.Dynamics.Nav.Runtime.NavTestPage"))
                .FirstOrDefault(t => t != null)
            ?? throw new InvalidOperationException(
                "NavTestPage type not found in any loaded assembly — cannot create TestPage target");

        // Find the internal 3-arg ctor: NavTestPage(ITreeObject, ApplicationObjectId, ITestPage)
        var ctor = _navTestPageType
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(c => c.GetParameters().Length == 3);
        if (ctor == null)
            throw new InvalidOperationException(
                "NavTestPage has no 3-arg constructor — Ncl shape changed");

        var page = CreateTestPageClient(self, objId);
        return ctor.Invoke(new object[] { self, objId, page });
    }

    private static Microsoft.Dynamics.Nav.Types.ITestPage CreateTestPageClient(object self, object objId)
    {
        var idProp = objId.GetType().GetProperty("ObjectNumber",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var pageId = idProp?.GetValue(objId) is int value ? value : 0;
        // Opt this page into a real metadata load — its parsed control tree, which is
        // what a control bound to a page VARIABLE (rather than to a Rec field) resolves
        // through. Null for a page the runner did not compile itself (no captured
        // metadata XML); such a page keeps the record-only behaviour below.
        var built = TestPageFactory.TryBuild(self, pageId, out var why);

        // Which of the three clients this page gets — see TestPageClientConstructionRule for
        // why "no record" is not the same question as "cannot be driven". A page with no
        // SourceTable used to land on the navigation mock, every one of whose members
        // answers a default, so a subpage part on such a host silently reported an empty
        // rowset under a directly-opened TestPage while the SAME part found its rows under
        // RunModal + [ModalPageHandler] (issue #2090) — the handler-driven construction
        // site, RunnerTestClientSession.GetPage, has built a live page over a null record
        // for this shape since #2007. Both sites now agree.
        switch (TestPageClientConstructionRule.Resolve(
                    recordBuilt: built != null,
                    pageIsParsed: RecordPatches.IsPageParsed(pageId),
                    pageDeclaresSourceTable: RecordPatches.PageDeclaresSourceTable(pageId)))
        {
            case TestPageClientKind.LiveOverRecord:
                return new LiveNavTestPage(built!.Record, RecordPatches.GetPageControlFieldMap(pageId),
                    RecordPatches.GetInsertAllowedForPage(pageId), built.Page, self, pageId);

            // LiveNavTestPage over a null record is not a silent fake: every Rec-dependent
            // member refuses BY NAME through RequireRecord if the AL actually reaches for
            // one, while parts, page-variable-bound controls, actions and the page's own
            // triggers — all a no-SourceTable page can legally offer — work.
            case TestPageClientKind.LiveRecordless
                when TestPageFactory.TryBuildRecordless(self, pageId) is { } recordless:
                return new LiveNavTestPage(null, RecordPatches.GetPageControlFieldMap(pageId),
                    RecordPatches.GetInsertAllowedForPage(pageId), recordless, self, pageId);
        }

        Console.Error.WriteLine($"[TestPage] {why}; using navigation mock.");
        return new MockITestPage();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static NCLMetaTable NavTestPageBase_GetMetaTable(object self)
    {
        var pageIdField = FindInstanceField(self.GetType(), "pageUnderTestId");
        var objId = pageIdField?.GetValue(self);
        var idProp = objId?.GetType().GetProperty("ObjectNumber",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var pageId = idProp?.GetValue(objId) is int value ? value : 0;

        // BC's own body returns null when the page's SourceObject.SourceTable is 0 — a page
        // without a source table is legal AL, and PrimaryKeyFields then reads as empty.
        // Mirror that, but ONLY for a page we actually parsed: answering null for a page the
        // parser never saw would turn a runner gap into a silently empty primary key.
        if (!RecordPatches.IsPageParsed(pageId))
            throw new InvalidOperationException(
                $"TestPage {pageId} was never parsed from AL source — cannot resolve its " +
                $"SourceTable, and guessing would silently yield an empty primary key.");

        if (!RecordPatches.PageDeclaresSourceTable(pageId))
            return null!;

        var tableId = RecordPatches.GetSourceTableIdForPage(pageId);
        if (tableId == 0)
            throw new InvalidOperationException($"TestPage {pageId} has no parsed SourceTable.");

        return RecordPatches.GetOrBuildNCLMetaTable(tableId)
            ?? throw new InvalidOperationException($"TestPage {pageId} SourceTable {tableId} has no NCLMetaTable.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool NavTestPageBase_ALGoToRecord(
        object self,
        Microsoft.Dynamics.Nav.Types.DataError errorLevel,
        NavRecord record)
    {
        var pageId = GetPageIdFromTestPage(self);
        var tableId = RecordPatches.GetSourceTableIdForPage(pageId);
        var pkFields = RecordPatches.GetPrimaryKeyFieldIdsForTable(tableId);
        if (pkFields.Length == 0)
        {
            // A page that ships PRECOMPILED (and its table) never went through the AL
            // parser, so both lookups above answer empty — and returning false here left
            // GoToRecord a silent no-op: the cursor never moved and every later Rec read
            // on the page saw an unpositioned record (issue #2057). BC's own ALGoToRecord
            // derives the key from the passed RECORD's compiled metadata
            // (NCLMetadata.GetMetaTableById(record.TableID).PrimaryKey), not from parsed
            // page source — mirror that.
            var metaTable = Microsoft.Dynamics.Nav.Runtime.NavGlobal.NCLMetadata
                .GetMetaTableById(record.TableID, requireCompiled: false);
            pkFields = metaTable?.PrimaryKey?.KeyFieldsList?
                .Select(f => f.FieldNo).ToArray() ?? Array.Empty<int>();
        }
        if (pkFields.Length == 0) return false;

        var testPageField = FindInstanceField(self.GetType(), "testPage");
        if (testPageField?.GetValue(self) is not Microsoft.Dynamics.Nav.Types.ITestPage testPage)
            return false;

        var values = pkFields.Select(fieldNo => (object)record.GetFieldValue(fieldNo)).ToArray();
        return testPage.FindRowFromTableFieldValues(pkFields, values, forward: true);
    }

    /// <summary>
    /// Persists a row started by TestPage.New() that has not been left yet. BC's
    /// NavTestPageBase.Close() drives the real client's "commit the row you are standing on"
    /// step, but it never calls back into ITestPage.Close(), so the runner's LiveNavTestPage
    /// never learned the page was closing and silently dropped the pending row. Cecil prepends
    /// a call to this helper onto NavTestPageBase.Close().
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavTestPageBase_FlushPendingNewRow(object self)
    {
        if (self == null) return;
        var testPageField = FindInstanceField(self.GetType(), "testPage");
        if (testPageField?.GetValue(self) is LiveNavTestPage live)
            live.FlushPendingNewRow();
    }

    private static int GetPageIdFromTestPage(object self)
    {
        var pageIdField = FindInstanceField(self.GetType(), "pageUnderTestId");
        var objId = pageIdField?.GetValue(self);
        var idProp = objId?.GetType().GetProperty("ObjectNumber",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return idProp?.GetValue(objId) is int value ? value : 0;
    }

    private static FieldInfo? FindInstanceField(Type type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var field = t.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (field != null) return field;
        }
        return null;
    }

    // Cache: form ID → generated Form Type.
    private static readonly ConcurrentDictionary<int, Type?> _formTypeCache = new();

    // Cache: report ID → generated Report Type.
    private static readonly ConcurrentDictionary<int, Type?> _reportTypeCache = new();

    /// <summary>Public accessor for NavReportSync.CreateReportInstance (the Cecil-rewritten
    /// NCLMetaReport.CreateObjectInstance body): report id → compiled Report{id} type.</summary>
    public static Type? FindReportTypePublic(int id) => _reportTypeCache.GetOrAdd(id, FindReportType);

    /// <summary>
    /// Replacement for NavFormHandle.CreateTarget().
    /// Same shape as NavCodeunitHandle/NavTestPageHandle — bypass NavGlobal.NCLMetadata
    /// by scanning the loaded test assembly for `Form{ID}` and constructing via the
    /// (ITreeObject, NavRecord) ctor when the page's SourceTable is resolvable, falling
    /// back to the 1-arg ITreeObject ctor otherwise.
    ///
    /// The 2-arg ctor matters for issue #1719: a plain `Page X` variable (as opposed to a
    /// TestPage, which already goes through RunnerPageInstance/TestPageFactory) built via
    /// the 1-arg ctor alone never gets its Rec bound — probed directly, `Rec` and the base
    /// `SourceTable` getter both read back null, no exception — so any Base App/System App
    /// page method that reads Rec before the page is ever "opened" (e.g. Page 700
    /// "Error Messages".SetRecords: `Rec.Copy(TempErrorMessage, true)`) NREs inside BC's own
    /// unmodified body before AL gets a chance to run. Binding Rec here, the same way
    /// RunnerPageInstance already does for TestPage, is a construction-time fix in our own
    /// dispatch — not a change to that body — per precompiled-dll-respect.md.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object NavFormHandle_CreateTarget(object self)
    {
        var objIdProp = self.GetType().GetProperty("ObjectId",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var objId = objIdProp!.GetValue(self)!;
        var idProp = objId.GetType().GetProperty("ObjectNumber",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        int id = (int)idProp!.GetValue(objId)!;

        var formType = _formTypeCache.GetOrAdd(id, FindFormType);
        if (formType == null)
            throw new InvalidOperationException(
                $"Page{id} is not present in the test assembly or any loaded dependency.");

        var ctors = formType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var twoArgCtor = ctors.FirstOrDefault(c => c.GetParameters().Length == 2
            && typeof(Microsoft.Dynamics.Nav.Runtime.ITreeObject).IsAssignableFrom(c.GetParameters()[0].ParameterType)
            && typeof(NavRecord).IsAssignableFrom(c.GetParameters()[1].ParameterType));
        if (twoArgCtor != null)
        {
            var tableId = RecordPatches.ResolveSourceTableIdForAnyPage(id);
            if (tableId != 0)
            {
                var isTemporary = RecordPatches.ResolveSourceTableTemporaryForAnyPage(id);
                var record = TestPageFactory.TryBuildBlankRecord(self, tableId, isTemporary, out var why);
                if (record != null)
                {
                    var instance = twoArgCtor.Invoke(new object?[] { self, record });
                    BindPageSourceObjectId(instance, tableId);

                    // NavForm.SetSourceTable(record, clone: false) is BC's own binding
                    // step — it stores `record` as Rec, wires the Notify handler and
                    // computes auto-calc fields. Reused rather than reimplemented so a
                    // page's real construction-time behaviour runs, not our guess at it.
                    var setSourceTable = instance.GetType().GetMethod("SetSourceTable",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        binder: null, types: new[] { typeof(NavRecord), typeof(bool) }, modifiers: null);
                    try { setSourceTable?.Invoke(instance, new object?[] { record, false }); }
                    catch (TargetInvocationException tie) when (tie.InnerException != null)
                    {
                        // Loud, not fatal: the instance is still usable exactly as it was
                        // before this fix (Rec unbound) — a page method that never touches
                        // Rec keeps working, and one that does gets the SAME NRE it always
                        // threw, not a new, worse failure from a half-bound instance.
                        Console.Error.WriteLine(
                            $"[BcRuntime] Page{id}: SetSourceTable failed after binding SourceTable {tableId} "
                            + $"({tie.InnerException.GetType().Name}: {tie.InnerException.Message}); "
                            + "Rec stays unbound, as before this fix.");
                    }
                    return instance;
                }
                if (Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_PAGE_METADATA") == "1")
                    Console.Out.WriteLine($"[NavFormHandle_CreateTarget] Page{id}: {why}; falling back to the 1-arg ctor");
            }
        }

        var ctor = ctors.FirstOrDefault(c => c.GetParameters().Length == 1 &&
                typeof(Microsoft.Dynamics.Nav.Runtime.ITreeObject)
                    .IsAssignableFrom(c.GetParameters()[0].ParameterType));
        if (ctor == null)
            throw new InvalidOperationException(
                $"Page{id} has no single-arg ITreeObject constructor");
        return ctor.Invoke(new object[] { self });
    }

    /// <summary>
    /// Stamp <c>NavForm.navSourceObject.SourceObjectId</c> to <paramref name="tableId"/>
    /// before <c>SetSourceTable</c> validates against it.
    ///
    /// Real BC resolves that field via <c>NavGlobal.NCLMetadata</c> at page construction —
    /// the same provider every other CreateTarget bypass in this file avoids because it NREs
    /// on a skeleton meta. Left unset it stays at its CLR default (0), and
    /// <c>SetSourceTable</c>'s own validation (`record.TableID == navSourceObject.
    /// SourceObjectId`) then throws <c>NavNCLTableIdMismatchException</c> before Rec is ever
    /// bound — this fills in exactly the state that provider would have supplied, using the
    /// SAME table id already resolved from the page's own AL/SymbolReference.json (never
    /// guessed), not a change to SetSourceTable's own logic.
    ///
    /// <c>NavForm.NavSourceObject</c> is a value type: a plain <c>FieldInfo.GetValue</c> +
    /// <c>PropertyInfo.SetValue</c> mutates a boxed COPY that is never written back, so the
    /// poke must be re-stamped onto the instance field explicitly.
    /// </summary>
    private static void BindPageSourceObjectId(object formInstance, int tableId)
    {
        var navSourceObjField = FindFieldUpHierarchy(formInstance.GetType(), "navSourceObject");
        var navSourceObj = navSourceObjField?.GetValue(formInstance);
        var sourceObjectIdProp = navSourceObj?.GetType().GetProperty("SourceObjectId",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (navSourceObjField == null || navSourceObj == null || sourceObjectIdProp is not { CanWrite: true })
            return;
        sourceObjectIdProp.SetValue(navSourceObj, tableId);
        if (navSourceObjField.FieldType.IsValueType)
            navSourceObjField.SetValue(formInstance, navSourceObj);
    }

    // One-time hook: NavReport..ctor(ITreeObject, Int32, NCLStaticMetadata) → same replacement
    // as NavApplicationObjectBase..ctor. Registered lazily on first report creation because
    // NavReport is in Ncl.dll which is loaded during ApplyAllPatches, but the ctor may not
    // yet be JIT-compiled when we get here — JmpHook.Apply patches the native code regardless.
    private static int _navReportCtorHookApplied;

    private static void EnsureNavReportCtorHooked()
    {
        if (System.Threading.Interlocked.Exchange(ref _navReportCtorHookApplied, 1) != 0) return;
        // NavReport..ctor(ITreeObject, Int32, NCLStaticMetadata) is now Cecil-rewritten in
        // NclCecilRewrite.cs (search "NavReport ctor") to:
        //   - chain DataItemIterator..ctor (preserves the `dataItems = new List<DataItem>()`
        //     field initializer needed by IC's `Add(dataItem, "...")` call)
        //   - skip `parent.Tree.Session.Company.RegisterReport(this)` (NREs on skeleton session)
        //   - keep `PreviewCanPrint = true` (auto-prop, safe).
        // A JmpHook here would replace the ENTIRE ctor including the DataItemIterator chain,
        // which would skip the dataItems field initializer and cause IC to NRE in Add.
        // Kept as a placeholder for future per-instance setup if needed.
    }

    // Cache: report types whose InitializeComponent has been no-op'd.
    private static readonly ConcurrentDictionary<Type, byte> _hookedReportInitComponents = new();

    private static void HookReportInitializeComponent(Type reportType)
    {
        if (!_hookedReportInitComponents.TryAdd(reportType, 0)) return;
        try
        {
            var m = reportType.GetMethod("InitializeComponent",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (m == null || m.DeclaringType != reportType) return; // no override in this type
            var repl = typeof(BcRuntime).GetMethod(nameof(NavReport_BeginEndInitialization),
                BindingFlags.Public | BindingFlags.Static)!;
            AlRunner.Infrastructure.JmpHook.Apply(m, repl, $"{reportType.Name}.InitializeComponent()");
            Console.Error.WriteLine($"[BcRuntime] {reportType.Name}.InitializeComponent() hooked → NoOp");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[BcRuntime] HookReportInitializeComponent({reportType.Name}) failed: {ex.Message}");
        }
    }

    /// <summary>No-op replacement for NavReport.BeginInitialization, EndInitialization,
    /// and Report{N}.InitializeComponent — these dereference Session.MetadataProvider /
    /// NCLMetaReport fields that are null on the skeleton, causing NREs identical to the
    /// NavXmlPort.BeginInitialization cluster already patched in XmlPortPatches.cs.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavReport_BeginEndInitialization(object self)
    {
    }

    /// <summary>
    /// Replacement for NavReportHandle.CreateTarget().
    /// Same shape as NavFormHandle: bypass NavGlobal.NCLMetadata.GetMetaReportById +
    /// NCLMetaReport.CreateObjectInstance (which NREs on a skeleton meta because
    /// ApplicationObjectConstructor returns null), and construct Report{ID} directly
    /// from the loaded test assembly via a 1-arg ITreeObject ctor.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object NavReportHandle_CreateTarget(object self)
    {
        EnsureNavReportCtorHooked();
        var objIdProp = self.GetType().GetProperty("ObjectId",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var objId = objIdProp!.GetValue(self)!;
        var idProp = objId.GetType().GetProperty("ObjectNumber",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        int id = (int)idProp!.GetValue(objId)!;

        var reportType = _reportTypeCache.GetOrAdd(id, FindReportType);
        if (reportType == null)
            throw new InvalidOperationException(
                $"Report{id} is not present in the test assembly or any loaded dependency.");
        // Hook this report type's InitializeComponent override to no-op before invoking the ctor.
        // NavReport.BeginInitialization is Cecil-rewritten to install a stub
        // MetaReport+MasterPage on `base.Metadata` (see NclCecilRewrite.cs §NavReport
        // block). With that stub in place, BC-emitted Report{N}.InitializeComponent
        // runs to completion: it creates NavRecordHandle + DataItems, binds
        // OnAfterGetRecord triggers, calls Add (Cecil → base.Add populates dataItems),
        // and finally `new RequestPage(this, Metadata.RequestFormMetadata)` which
        // returns our stubbed MasterPage. DO NOT hook IC to a no-op or DataItems
        // never populates and per-row triggers can't fire.
        // -- HookReportInitializeComponent(reportType);   // deliberately not hooked
        // BC emits report ctors as either:
        //   (ITreeObject parent)
        //   (ITreeObject parent, NCLMetaReport metadata)
        // depending on AL features used. Try 1-arg first, then 2-arg with metadata
        // pulled from our populated SystemTenant cache (skeleton OK — NavReport.ctor
        // tolerates the cache-built skeleton entry).
        var ctors = reportType.GetConstructors();
        object instance;
        var oneArg = ctors.FirstOrDefault(c => c.GetParameters().Length == 1 &&
            typeof(Microsoft.Dynamics.Nav.Runtime.ITreeObject)
                .IsAssignableFrom(c.GetParameters()[0].ParameterType));
        if (oneArg != null)
        {
            instance = oneArg.Invoke(new object[] { self });
        }
        else
        {
            var twoArg = ctors.FirstOrDefault(c => c.GetParameters().Length == 2 &&
                typeof(Microsoft.Dynamics.Nav.Runtime.ITreeObject)
                    .IsAssignableFrom(c.GetParameters()[0].ParameterType));
            if (twoArg == null)
                throw new InvalidOperationException(
                    $"Report{id} has no (ITreeObject) or (ITreeObject, NCLMetaReport) constructor");
            var metaParamType = twoArg.GetParameters()[1].ParameterType;
            var metaArg = LookupNclMetaForReport(id, metaParamType);
            instance = twoArg.Invoke(new object?[] { self, metaArg });
        }

        // Run the same post-construction steps BC's NCLMetaReport.CreateObjectInstance
        // runs — this path bypasses that method, and without FinalizeDataItemLoading the
        // report's TableViewIsSet array stays null, so AL's Report.SetTableView(Rec) NREs
        // inside BC's own DataItemIterator.SetTableView (issue #1718).
        AlRunner.NavReportSync.CompleteReportConstruction(instance, self, id);
        return instance;
    }

    private static object? LookupNclMetaForReport(int id, Type expectedMetaType)
    {
        // Primary: NavGlobal.NCLMetadata.GetMetaReportById (the real global metadata).
        // This succeeds only when BC has registered the report in its own metadata store,
        // which does not happen in runner mode — so we expect null or an exception here.
        var navNcl = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        var navGlobal = navNcl?.GetType("Microsoft.Dynamics.Nav.Runtime.NavGlobal");
        var nclMeta = navGlobal?.GetProperty("NCLMetadata", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (nclMeta != null)
        {
            var getMeta = nclMeta.GetType().GetMethod("GetMetaReportById",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[] { typeof(int) }, null);
            try
            {
                var primary = getMeta?.Invoke(nclMeta, new object[] { id });
                if (primary != null) return primary;
            }
            catch { /* NavNCLApplicationObjectNotFoundException expected — fall through */ }
        }

        // Fallback: build a skeleton NCLMetaReport via CreateEmptyNCLMetaReport (internal static
        // factory). This succeeds for any numeric report ID and gives the Report{N}..ctor a
        // non-null metadata argument, preventing the NRE observed in the Report*..ctor cluster.
        return _metaReportFallbackCache.GetOrAdd(id, static reportId =>
        {
            try
            {
                var nclAsm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
                var tMeta = nclAsm?.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaReport");
                var factory = tMeta?.GetMethod("CreateEmptyNCLMetaReport",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (factory == null)
                    throw new InvalidOperationException("CreateEmptyNCLMetaReport not found on NCLMetaReport");

                var tAppGroup = nclAsm!.GetType("Microsoft.Dynamics.Nav.Runtime.Apps.NavAppGroup");
                var baseGroup = tAppGroup?.GetProperty("BaseGroup",
                        BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                    ?? tAppGroup?.GetField("BaseGroup",
                        BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

                var meta = factory.Invoke(null, new object?[] { null, reportId, baseGroup, -1, string.Empty });
                if (meta == null)
                    throw new InvalidOperationException($"CreateEmptyNCLMetaReport returned null for Report{reportId}");

                // Mark metadataLoaded=true so the JMP-hooked Populate path is never entered.
                var tBase = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaApplicationObject");
                var fLoaded = tBase?.GetField("metadataLoaded",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (fLoaded != null)
                    AlRunner.Infrastructure.FieldPoke.SetInstance(fLoaded, meta, true);

                Console.Error.WriteLine($"[BcRuntime] LookupNclMetaForReport({reportId}): built skeleton NCLMetaReport via fallback");
                return meta;
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
                throw new InvalidOperationException(
                    $"LookupNclMetaForReport fallback failed for Report{reportId}: {inner.GetType().Name}: {inner.Message}", inner);
            }
        });
    }

    // Cache: query ID → generated NavQuery Type.
    private static readonly ConcurrentDictionary<int, Type?> _queryTypeCache = new();

    /// <summary>
    /// Replacement for NavQueryHandle.CreateTarget(). Same shape as
    /// NavReportHandle/NavFormHandle: bypass NavGlobal.NCLMetadata.GetMetaQueryById +
    /// NCLMetaQuery.CreateObjectInstance (which NREs on a skeleton meta because
    /// ApplicationObjectConstructor returns null), and construct Query{ID} directly
    /// from the loaded test assembly.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object NavQueryHandle_CreateTarget(object self)
    {
        var objIdProp = self.GetType().GetProperty("ObjectId",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var objId = objIdProp!.GetValue(self)!;
        var idProp = objId.GetType().GetProperty("ObjectNumber",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        int id = (int)idProp!.GetValue(objId)!;

        return ConstructQuery(id, self);
    }

    /// <summary>
    /// Replacement for <c>NCLMetaQuery.CreateObjectInstance(ITreeObject, SecurityFiltering)</c>.
    ///
    /// Same story as NCLMetaXmlPort.CreateObjectInstance: BC invokes
    /// <c>base.ApplicationObjectConstructor</c>, which the runner forces null for every
    /// object type, substituting a per-type construction path. Query had one only for the
    /// HANDLE form (an AL <c>Query "Foo"</c> variable). The STATIC forms —
    /// <c>Query.SaveAsXml(id, stream)</c>, <c>SaveAsCsv(id, …)</c>, <c>SaveAsJson(id, …)</c> —
    /// come through here and NREd on the null delegate.
    ///
    /// The securityFiltering argument is dropped deliberately: the AL-emitted
    /// <c>Query{id}</c> constructors the handle path already uses do not take one, and the
    /// runner's security filtering is applied on the record side (see
    /// SecurityFilteringPatches), not on the query object.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Microsoft.Dynamics.Nav.Runtime.NavQuery NCLMetaQuery_CreateObjectInstance(
        object self, Microsoft.Dynamics.Nav.Runtime.ITreeObject parent, int securityFiltering)
    {
        const BindingFlags Any =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        int id = 0;
        foreach (var propName in new[] { "ApplicationObjectId", "ObjectId" })
        {
            var objId = self.GetType().GetProperty(propName, Any)?.GetValue(self);
            if (objId?.GetType().GetProperty("ObjectNumber", Any)?.GetValue(objId) is int n && n != 0)
            {
                id = n;
                break;
            }
        }
        if (id == 0)
            throw new InvalidOperationException(
                "NCLMetaQuery.CreateObjectInstance: could not read the query's object id from " +
                "its metadata — constructing the wrong query would silently export the wrong dataset.");

        return (Microsoft.Dynamics.Nav.Runtime.NavQuery)ConstructQuery(id, parent);
    }

    /// <summary>
    /// Construct the AL-emitted <c>Query{id}</c> instance parented to <paramref name="parent"/>.
    /// Shared by the handle path and the static-form path so the two cannot diverge.
    /// </summary>
    private static object ConstructQuery(int id, object self)
    {
        var queryType = _queryTypeCache.GetOrAdd(id, FindQueryType);
        if (queryType == null)
            throw new InvalidOperationException(
                $"Query{id} is not present in the test assembly or any loaded dependency.");

        // TEMP DIAGNOSTIC (qdiag): log which ctor path is taken and whether the
        // resulting NavQuery has a usable NCLMetaQuery / QueryDefinition.
        object QDiag(int arity, object q)
        {
            if (Environment.GetEnvironmentVariable("AL_RUNNER_QDIAG") != "1") return q;
            try
            {
                var mqProp = q.GetType().GetProperty("NCLMetaQuery",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var mq = mqProp?.GetValue(q);
                string qd;
                if (mq == null) qd = "NCLMetaQuery=NULL";
                else
                {
                    try
                    {
                        var qdProp = mq.GetType().GetProperty("QueryDefinition",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        var qdv = qdProp?.GetValue(mq);
                        qd = $"NCLMetaQuery={mq.GetType().Name} QueryDefinition={(qdv == null ? "null" : qdv.GetType().Name)}";
                    }
                    catch (Exception ex)
                    {
                        var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
                        qd = $"NCLMetaQuery={mq.GetType().Name} QueryDefinition THREW {inner.GetType().Name}: {inner.Message}";
                    }
                }
                File.AppendAllText("/tmp/qdiag.txt", $"Query{id}: ctor arity={arity}; {qd}\n");
            }
            catch (Exception ex) { File.AppendAllText("/tmp/qdiag.txt", $"Query{id}: QDiag failed: {ex.Message}\n"); }
            return q;
        }

        // Try ctor signatures matching NavQuery's three protected constructors:
        //   (ITreeObject parent)                          — AL-emitted convenience
        //   (ITreeObject parent, SecurityFiltering, NCLMetaQuery)
        //   (ITreeObject parent, int objectId, SecurityFiltering)
        var ctors = queryType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        var oneArg = ctors.FirstOrDefault(c => c.GetParameters().Length == 1 &&
            typeof(Microsoft.Dynamics.Nav.Runtime.ITreeObject)
                .IsAssignableFrom(c.GetParameters()[0].ParameterType));
        if (oneArg != null) return QDiag(1, oneArg.Invoke(new object[] { self }));

        var twoArg = ctors.FirstOrDefault(c => c.GetParameters().Length == 2 &&
            typeof(Microsoft.Dynamics.Nav.Runtime.ITreeObject)
                .IsAssignableFrom(c.GetParameters()[0].ParameterType));
        if (twoArg != null) return QDiag(2, twoArg.Invoke(new object?[] { self, null }));

        // 3-arg (ITreeObject, SecurityFiltering, NCLMetaQuery): NavQuery..ctor was
        // Cecil-rewritten to be null-safe (skips metadata access), so we can pass
        // null as metaQuery and SecurityFiltering=Disabled.
        var threeArgMq = ctors.FirstOrDefault(c =>
        {
            var ps = c.GetParameters();
            return ps.Length == 3 &&
                typeof(Microsoft.Dynamics.Nav.Runtime.ITreeObject).IsAssignableFrom(ps[0].ParameterType) &&
                ps[1].ParameterType.FullName == "Microsoft.Dynamics.Nav.Runtime.SecurityFiltering" &&
                ps[2].ParameterType.Name == "NCLMetaQuery";
        });
        if (threeArgMq != null)
        {
            var secFilt = threeArgMq.GetParameters()[1].ParameterType;
            // SecurityFiltering.Disabled = 0 (first enum member); use 0 as a safe default.
            var secVal = Enum.ToObject(secFilt, 0);
            // Build a REAL NCLMetaQuery so the genuine async engine can execute the
            // query against the in-memory provider (null → NRE in FindDataImplAsync).
            var meta = AlRunner.Patches.RecordPatches.BuildRealNCLMetaQuery(id, queryType);
            return QDiag(31, threeArgMq.Invoke(new object?[] { self, secVal, meta }));
        }

        // 4-arg (ITreeObject, int, SecurityFiltering, NCLMetaQuery): chains to the
        // 3-arg null-safe ctor in NavQuery base, so passing null metaQuery is OK.
        var fourArg = ctors.FirstOrDefault(c =>
        {
            var ps = c.GetParameters();
            return ps.Length == 4 &&
                typeof(Microsoft.Dynamics.Nav.Runtime.ITreeObject).IsAssignableFrom(ps[0].ParameterType) &&
                ps[1].ParameterType == typeof(int) &&
                ps[2].ParameterType.FullName == "Microsoft.Dynamics.Nav.Runtime.SecurityFiltering" &&
                ps[3].ParameterType.Name == "NCLMetaQuery";
        });
        if (fourArg != null)
        {
            var secFilt = fourArg.GetParameters()[2].ParameterType;
            var secVal = Enum.ToObject(secFilt, 0);
            return QDiag(4, fourArg.Invoke(new object?[] { self, id, secVal, null }));
        }

        // 3-arg (ITreeObject, int, SecurityFiltering): also Cecil-rewritten to skip metadata.
        var threeArgInt = ctors.FirstOrDefault(c =>
        {
            var ps = c.GetParameters();
            return ps.Length == 3 &&
                typeof(Microsoft.Dynamics.Nav.Runtime.ITreeObject).IsAssignableFrom(ps[0].ParameterType) &&
                ps[1].ParameterType == typeof(int) &&
                ps[2].ParameterType.FullName == "Microsoft.Dynamics.Nav.Runtime.SecurityFiltering";
        });
        if (threeArgInt != null)
        {
            var secFilt = threeArgInt.GetParameters()[2].ParameterType;
            var secVal = Enum.ToObject(secFilt, 0);
            return QDiag(32, threeArgInt.Invoke(new object?[] { self, id, secVal }));
        }

        var sigs = string.Join(" | ", ctors.Select(c =>
            "(" + string.Join(",", c.GetParameters().Select(p => p.ParameterType.Name)) + ")"));
        throw new InvalidOperationException(
            $"Query{id} has no recognized constructor. Available: {sigs}");
    }

    internal static Type? FindQueryType(int id)
    {
        var navNcl = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        Type? queryBase = navNcl?.GetType("Microsoft.Dynamics.Nav.Runtime.NavQuery");
        var name = $"Query{id}";
        if (_currentTestAssembly != null)
        {
            try
            {
                var t = AlRunner.Infrastructure.AssemblyTypeIndex.For(_currentTestAssembly)
                    .FindFirst(name, x => (queryBase == null || queryBase.IsAssignableFrom(x)));
                if (t != null) return t;
            }
            catch { }
        }
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm == _currentTestAssembly) continue;
            // Skip a previous-cycle generation of a SIBLING/dependency app —
            // otherwise a cross-app call can bind to the stale generation even
            // though CurrentTestAssembly correctly points at the fresh copy of the
            // app actually executing right now (issue #1901).
            if (IsStaleBundleAssembly(asm)) continue;
            try
            {
                var t = AlRunner.Infrastructure.AssemblyTypeIndex.For(asm)
                    .FindFirst(name, x => (queryBase == null || queryBase.IsAssignableFrom(x)));
                if (t != null) return t;
            }
            catch { }
        }
        return null;
    }

    private static Type? FindReportType(int id)
    {
        var navNcl = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        Type? reportBase = navNcl?.GetType("Microsoft.Dynamics.Nav.Runtime.NavReport");
        var name = $"Report{id}";
        if (_currentTestAssembly != null)
        {
            try
            {
                var t = AlRunner.Infrastructure.AssemblyTypeIndex.For(_currentTestAssembly)
                    .FindFirst(name, x => (reportBase == null || reportBase.IsAssignableFrom(x)));
                if (t != null) return t;
            }
            catch { }
        }
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm == _currentTestAssembly) continue;
            // Skip a previous-cycle generation of a SIBLING/dependency app —
            // otherwise a cross-app call can bind to the stale generation even
            // though CurrentTestAssembly correctly points at the fresh copy of the
            // app actually executing right now (issue #1901).
            if (IsStaleBundleAssembly(asm)) continue;
            try
            {
                var t = AlRunner.Infrastructure.AssemblyTypeIndex.For(asm)
                    .FindFirst(name, x => (reportBase == null || reportBase.IsAssignableFrom(x)));
                if (t != null) return t;
            }
            catch { }
        }
        return null;
    }

    private static Type? FindFormType(int id)
    {
        // BC's Compilation.Emit produces `class Page{id} : NavForm` for AL `page` objects
        // (the NavForm base class is what the runtime calls "form", but the C# class name
        // is `Page{id}`). The "Form{id}" name was a false guess in the §P session.
        var navNcl = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        Type? formBase = navNcl?.GetType("Microsoft.Dynamics.Nav.Runtime.NavForm");
        var name = $"Page{id}";
        if (_currentTestAssembly != null)
        {
            try
            {
                var t = AlRunner.Infrastructure.AssemblyTypeIndex.For(_currentTestAssembly)
                    .FindFirst(name, x => (formBase == null || formBase.IsAssignableFrom(x)));
                if (t != null) return t;
            }
            catch { }
        }
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm == _currentTestAssembly) continue;
            // Skip a previous-cycle generation of a SIBLING/dependency app —
            // otherwise a cross-app call can bind to the stale generation even
            // though CurrentTestAssembly correctly points at the fresh copy of the
            // app actually executing right now (issue #1901).
            if (IsStaleBundleAssembly(asm)) continue;
            try
            {
                var t = AlRunner.Infrastructure.AssemblyTypeIndex.For(asm)
                    .FindFirst(name, x => (formBase == null || formBase.IsAssignableFrom(x)));
                if (t != null) return t;
            }
            catch { }
        }
        return null;
    }

    private static Type? FindTestPageType(int id)
    {
        // BC's Compilation.Emit produces `class Page{id} : NavForm` even for AL pages
        // that test code references via `TestPage "Name"` — there is NO separate
        // TestPage{id} class. NavTestPageHandle wraps the same Page{id} behaviorally.
        // Match by NavForm base (NavTestPage doesn't exist as a real type).
        var navNcl = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        Type? testPageBase = navNcl?.GetType("Microsoft.Dynamics.Nav.Runtime.NavForm");
        var name = $"Page{id}";
        if (_currentTestAssembly != null)
        {
            try
            {
                var t = AlRunner.Infrastructure.AssemblyTypeIndex.For(_currentTestAssembly)
                    .FindFirst(name, x => (testPageBase == null || testPageBase.IsAssignableFrom(x)));
                if (t != null) return t;
            }
            catch { }
        }
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm == _currentTestAssembly) continue;
            // Skip a previous-cycle generation of a SIBLING/dependency app —
            // otherwise a cross-app call can bind to the stale generation even
            // though CurrentTestAssembly correctly points at the fresh copy of the
            // app actually executing right now (issue #1901).
            if (IsStaleBundleAssembly(asm)) continue;
            try
            {
                var t = AlRunner.Infrastructure.AssemblyTypeIndex.For(asm)
                    .FindFirst(name, x => (testPageBase == null || testPageBase.IsAssignableFrom(x)));
                if (t != null) return t;
            }
            catch { }
        }
        return null;
    }

    // Well-known codeunit IDs that ship inside Microsoft dependency apps. When the
    // test compile resolves a `Codeunit X` reference against a Microsoft .app at
    // symbol-resolution time but the runtime has no DLL for that .app loaded,
    // CreateTarget can't find the type. We surface the dependency name instead of
    // a bare numeric id so the user knows which dependency is missing.
    private static readonly Dictionary<int, (string Name, string Package)> _knownDependencyCodeunits = new()
    {
        [130000] = ("Assert",                  "Library Assert (test framework)"),
        [130002] = ("Library Assert",          "Library Assert (test framework)"),
        [130440] = ("Library Variable Storage","Library Variable Storage (test framework)"),
        [130500] = ("Test Runner",             "Test Runner (test framework)"),
        [131000] = ("Library - Test Initialize","Library - Test Initialize (test framework)"),
        [310]    = ("No. Series",              "Base Application"),
    };

    private static string BuildMissingCodeunitMessage(int id)
    {
        if (_knownDependencyCodeunits.TryGetValue(id, out var known))
        {
            return
                $"Codeunit {id} (\"{known.Name}\") is not present in the test " +
                $"assembly or any loaded dependency. It belongs to {known.Package}, " +
                $"a Microsoft dependency app. AL Runner does not yet load " +
                $"Microsoft R2R packages at runtime — either provide an AL " +
                $"implementation/stub for this codeunit at the bucket level, or " +
                $"wait until runtime dependency loading lands.";
        }
        return
            $"Codeunit {id} is not present in the test assembly or any loaded " +
            $"dependency. It is most likely defined in a dependency .app " +
            $"(e.g. System Application, Base Application, a test-framework " +
            $"library, or a third-party app) whose runtime DLL is not loaded. " +
            $"AL Runner does not yet load dependency-app DLLs at runtime — " +
            $"either provide an AL implementation/stub at the bucket level, " +
            $"or wait until runtime dependency loading lands.";
    }

    /// <summary>
    /// Public accessor used by other patches (e.g. SessionPatches.AlRunnerStartSession)
    /// that need to resolve a codeunit type by AL object id without duplicating the
    /// test-assembly-first / loaded-assembly-fallback scan logic.
    /// </summary>
    public static Type? FindCodeunitTypePublic(int id)
        => _codeunitTypeCache.GetOrAdd(id, FindCodeunitType);

    private static Type? FindCodeunitType(int id)
    {
        var baseCu = typeof(Microsoft.Dynamics.Nav.Runtime.NavCodeunit);
        var name = $"Codeunit{id}";
        // Search the current test assembly first (avoids cross-bucket ID collisions).
        if (_currentTestAssembly != null)
        {
            try
            {
                var t = AlRunner.Infrastructure.AssemblyTypeIndex.For(_currentTestAssembly)
                    .FindFirst(name, baseCu.IsAssignableFrom);
                if (t != null) return t;
            }
            catch { }
        }
        // Fall back to all loaded assemblies (e.g. stubs in other assemblies).
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm == _currentTestAssembly) continue;
            // Skip a previous-cycle generation of a SIBLING/dependency app —
            // otherwise a cross-app call can bind to the stale generation even
            // though CurrentTestAssembly correctly points at the fresh copy of the
            // app actually executing right now (issue #1901).
            if (IsStaleBundleAssembly(asm)) continue;
            try
            {
                var t = AlRunner.Infrastructure.AssemblyTypeIndex.For(asm)
                    .FindFirst(name, baseCu.IsAssignableFrom);
                if (t != null) return t;
            }
            catch { /* skip dynamic/reflection-only assemblies */ }
        }
        // Tier 3 — DLL-first dependency code. Microsoft test-toolkit codeunits ship
        // symbol-only (no runtime code in the .app); their compiled bodies live in the
        // extracted BC service-tier DLL cache. Lazily load the owning DLL by object id
        // so the test exercises the REAL Microsoft code rather than a re-compile.
        var fromCache = AlRunner.Infrastructure.ServiceTierDllIndex.ResolveObjectType(name);
        if (fromCache != null && baseCu.IsAssignableFrom(fromCache))
            return fromCache;
        return null;
    }
}

/// <summary>
/// No-op NavCodeunit subclass returned by NavCodeunitHandle_CreateTarget when an
/// AL caller invokes a codeunit in the system range (1-9999) or test-toolkit range
/// (130000-139999) that is not present in the test assembly. RunAsync (via the
/// hooked NavCodeunit_DoRunAsync) reflects for an OnRun method; this class has
/// none, so the call returns successfully without doing anything. This honors the
/// runner contract documented in tests/bucket-1/codeunit-runtime/128-codeunit-not-found.
/// </summary>
public sealed class NoOpCodeunit : Microsoft.Dynamics.Nav.Runtime.NavCodeunit
{
    public NoOpCodeunit(Microsoft.Dynamics.Nav.Runtime.ITreeObject parent, int objectId)
        : base(parent, objectId) { }
}
