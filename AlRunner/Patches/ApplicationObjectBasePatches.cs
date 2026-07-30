// ApplicationObjectBasePatches — replacements for NavApplicationObjectBase ctor + TryInvoke.
//
// Every AL codeunit/page/report inherits from NavApplicationObjectBase. The real ctor
// reads session/app-group state from the BC service tier; we rebuild the equivalent
// state pointing at the skeleton session.
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;

namespace AlRunner;

public static partial class BcRuntime
{
    /// <summary>
    /// Replacement for NavApplicationObjectBase(ITreeObject parent, ApplicationObjectId objectId, NCLStaticMetadata staticMetadata).
    /// The real ctor body does three problematic things:
    ///   1. `session = base.Tree.Session` — returns null because our skeleton tree has no session chain.
    ///   2. `NavCurrentThread.ResolveAppGroup(session)` — NREs through NCLMetadata on null session.
    ///   3. `base(parent)` chain call — this IS included in the method body and we must replicate it,
    ///      otherwise TreeObject.ctor (which sets `this.tree`) is never called.
    /// Our replacement: call CreateTreeHandler to set the tree, inject _skeletonSession, skip ResolveAppGroup.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavApplicationObjectBaseCtorReplacement(object self, object parent, object objectId, object? staticMetadata)
    {
        // 1. Replicate TreeObject.ctor: create the TreeHandler from parent and assign to this.tree.
        //    This is what the `base(parent)` chain normally does for NavApplicationObjectBase.
        if (_mCreateTreeHandler != null && _fNavComplexValueTree != null)
        {
            // Use parent if it has a valid tree; otherwise fall back to _skeletonRootScope.
            // This ensures every NavRecord/NavApplicationObjectBase always gets a non-null tree field,
            // which is required for TreeObject.IsDisposed (called from RecordImplementation.IsOpen).
            var parentAsTreeObject = parent as Microsoft.Dynamics.Nav.Runtime.ITreeObject;
            var effectiveParent = (parentAsTreeObject?.Tree != null)
                ? parentAsTreeObject
                : (Microsoft.Dynamics.Nav.Runtime.ITreeObject?)_skeletonRootScope;
            if (effectiveParent != null)
            {
                try
                {
                    var handler = _mCreateTreeHandler.Invoke(null, new object[] { effectiveParent, self });
                    FieldPoke.SetInstance(_fNavComplexValueTree, self, handler);
                    // Defensive: if the parent chain has a null session (e.g. parent is a
                    // NavCodeunitHandle whose TreeObjectReferenceHandler was built via a path
                    // that bypassed our root-tree seeding), the new handler inherits null
                    // session. Plant _skeletonSession so downstream code that reads
                    // `base.Tree.Session.X` (NavCodeunit.BindSubscription, etc.) finds it.
                    if (_fTreeHandlerSession != null && handler != null
                        && _fTreeHandlerSession.GetValue(handler) == null)
                    {
                        FieldPoke.SetInstance(_fTreeHandlerSession, handler, _skeletonSession!);
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine($"[AoCtor] tree creation failed for {self?.GetType().Name}: {ex.Message}"); }
            }
        }
        // 1b. Replicate `this.objectId = objectId` from the real ctor. The field is a readonly
        //     ApplicationObjectId struct; FieldInfo.SetValue with the boxed struct copies it
        //     (same mechanism StampObjectId already uses for NavRecord). Without this every
        //     runner-constructed codeunit/page/report has ObjectId.ObjectNumber == 0, which
        //     breaks any identity check on the handle — e.g. NavCodeunitHandle.ALAssign throws
        //     NavNCLNotSupportedOperationException("ObjectId != other.ObjectId") on
        //     `List of [Codeunit]`.Get because the element handle (built from Target.ObjectId==0)
        //     never matches the destination variable's real id. Faithful: this IS the real
        //     ctor's assignment, with the caller-supplied value.
        if (_fAoObjectId != null && objectId != null)
        {
            try { FieldPoke.SetInstance(_fAoObjectId, self, objectId); }
            catch (Exception ex) { Console.Error.WriteLine($"[AoCtor] objectId stamp failed for {self?.GetType().Name}: {ex.Message}"); }
        }
        // 2. Inject skeleton session instead of `session = base.Tree.Session` (which gives null).
        if (_fAoSession != null)
        {
            FieldPoke.SetInstance(_fAoSession, self, _skeletonSession);
            // Verify: read back the session field immediately to confirm write succeeded.
            var check = _fAoSession.GetValue(self);
            if (check == null)
                Console.Error.WriteLine($"[BcRuntime] WARN: session field write failed on {self.GetType().Name}");
        }
        else
        {
            Console.Error.WriteLine("[BcRuntime] WARN: _fAoSession is null — cannot inject session");
        }
        // 3. Skip NavCurrentThread.ResolveAppGroup — use BaseGroupId=0.
        if (_fAoOrigGroupId != null)    FieldPoke.SetInstance(_fAoOrigGroupId,    self, 0);
        if (_fAoRuntimeGroupId != null) FieldPoke.SetInstance(_fAoRuntimeGroupId, self, 0);
    }

    private static FieldInfo? _fAoExecPermValidated; // NavApplicationObjectBase.executePermissionsValidated : bool?

    /// <summary>
    /// Replacement for NavApplicationObjectBase.get_ExecutePermissionsValidatedEx.
    /// The real getter/setter consult session.Database.PermissionSetupMonitor.SetupVersion
    /// (live permission-change invalidation — a service-tier feature; permissions are
    /// static in the headless runner, so plain backing-field semantics are observably
    /// equivalent). session.Database is null on the skeleton → NRE without this.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool? NavAppObjBase_GetExecutePermissionsValidatedEx(object self)
    {
        _fAoExecPermValidated ??= self.GetType()
            .GetField("executePermissionsValidated", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? FindFieldUpHierarchy(self.GetType(), "executePermissionsValidated");
        return (bool?)_fAoExecPermValidated?.GetValue(self);
    }

    /// <summary>Setter counterpart — writes the backing field, skips the (skeleton-null)
    /// PermissionSetupMonitor version stamp.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavAppObjBase_SetExecutePermissionsValidatedEx(object self, bool? value)
    {
        _fAoExecPermValidated ??= FindFieldUpHierarchy(self.GetType(), "executePermissionsValidated");
        if (_fAoExecPermValidated != null)
            _fAoExecPermValidated.SetValue(self, value);
    }

    private static FieldInfo? FindFieldUpHierarchy(Type? t, string name)
    {
        while (t != null)
        {
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (f != null) return f;
            t = t.BaseType;
        }
        return null;
    }

    private static FieldInfo? _fSysCuHandle;      // NavSystemCodeunit.codeunitHandle
    private static PropertyInfo? _pCuHandleTarget; // NavCodeunitHandle.Target
    private static MethodInfo? _mCuInvokeAsync;   // NavCodeunit.InvokeAsync(int, object[])

    /// <summary>
    /// Replacement for NavSystemCodeunit.InvokeAsync(int, object[]).
    /// The real body wraps the dispatch in NavAppUsageEventSuppressionScope +
    /// diagnostics that walk skeleton-null session state. The DISPATCH itself is
    /// what matters (system codeunit 2000000005 'ReportingTriggers' etc. — real
    /// AL bodies compiled into Ncl, firing real IntegrationEvents through the
    /// runner's universal subscriber dispatch). Resolve the handle target and
    /// invoke it directly.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ValueTask<object> NavSystemCodeunit_InvokeAsync(object self, int methodId, object[] arguments)
    {
        _fSysCuHandle ??= FindFieldUpHierarchy(self.GetType(), "codeunitHandle")
            ?? throw new InvalidOperationException("NavSystemCodeunit.codeunitHandle field not found");
        var handle = _fSysCuHandle.GetValue(self)
            ?? throw new InvalidOperationException($"{self.GetType().Name}.codeunitHandle is null");
        _pCuHandleTarget ??= handle.GetType().GetProperty("Target",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("NavCodeunitHandle.Target property not found");
        object target;
        try { target = _pCuHandleTarget.GetValue(handle)
            ?? throw new InvalidOperationException("NavCodeunitHandle.Target is null"); }
        catch (TargetInvocationException tie) when (tie.InnerException != null) { throw tie.InnerException; }

        NavAppObjBase_SetExecutePermissionsValidatedEx(target, true);

        _mCuInvokeAsync ??= target.GetType().GetMethod("InvokeAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(int), typeof(object[]) }, null)
            ?? throw new InvalidOperationException($"{target.GetType().Name}.InvokeAsync(int,object[]) not found");
        try
        {
            return (ValueTask<object>)_mCuInvokeAsync.Invoke(target, new object[] { methodId, arguments })!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;
        }
    }

    /// <summary>
    /// Replacement for NavApplicationObjectBase.TryInvoke(NavSession session, Action method).
    /// The real body calls session.CurrentMethodScope.GetTryMethodScope() which NREs on the
    /// skeleton session.  We run the method directly, catching trappable AL exceptions.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool NavApplicationObjectBase_TryInvoke(object? session, Action? method)
    {
        if (method == null) return false;
        try
        {
            method();
            return true;
        }
        catch (Exception ex)
        {
            // Rethrow untrappable errors; swallow trappable NavBaseExceptions.
            if (ex is Microsoft.Dynamics.Nav.Types.Exceptions.NavBaseException nbe && !nbe.UntrappableError)
                return false;
            if (IsPermanentOutOfScope(ex, out var oos)) { ReportOosTrappedByTryFunction(oos!); return false; }
            throw;
        }
    }

    /// <summary>
    /// True when <paramref name="ex"/> is a RunnerOutOfScopeException for a surface that
    /// is PERMANENTLY out of scope (SMTP, external HTTP, an Azure Key Vault, …) rather
    /// than one that is in scope but not yet built (reason "not-yet-implemented").
    ///
    /// The distinction decides whether an AL [TryFunction] may trap it. A permanently
    /// out-of-scope surface does not exist in the runner's world at all — and a real BC
    /// environment that also lacks it raises a trappable error there, so the AL author's
    /// TryFunction sees `false`. Trapping it is therefore FAITHFUL, not a silent default:
    /// it reproduces the observable BC outcome. A "not-yet-implemented" surface is the
    /// opposite — it is a runner gap, and swallowing it would turn the gap into a green
    /// test that lies (see .claude/rules/loud-failures.md), so it keeps propagating.
    /// </summary>
    private static bool IsPermanentOutOfScope(Exception ex, out AlRunner.Infrastructure.RunnerOutOfScopeException? oos)
    {
        oos = ex as AlRunner.Infrastructure.RunnerOutOfScopeException;
        return oos != null
            && !oos.Reason.StartsWith("not-yet-implemented", StringComparison.Ordinal);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _oosTrapReported = new();

    /// <summary>
    /// Never silent: print one loud, named line per distinct (api, reason) the first time a
    /// TryFunction absorbs it, so the surface a test quietly did without is always visible in
    /// the run output even though the test itself legitimately continues.
    /// </summary>
    private static void ReportOosTrappedByTryFunction(AlRunner.Infrastructure.RunnerOutOfScopeException oos)
    {
        if (!_oosTrapReported.TryAdd($"{oos.Api} {oos.Reason}", 0)) return;
        Console.Error.WriteLine(
            $"[oos-in-try] {oos.Api} — {oos.Reason} — reached inside an AL [TryFunction]; " +
            $"returning false, which is what real BC does in an environment that also lacks " +
            $"this surface. Reported once per surface.");
    }

    /// <summary>
    /// Replacement for NavApplicationObjectBase.TryInvokeAsync(NavSession session, Func&lt;ValueTask&gt; method).
    /// The real async state machine calls session.CurrentMethodScope.GetTryMethodScope() which NREs
    /// on the skeleton session. We invoke the delegate synchronously (all BC code in the runner
    /// runs sync on a single AL thread) catching only trappable NavBaseExceptions, matching the
    /// faithful semantics of TryInvoke. Once this works, code that calls into the Azure Key Vault
    /// SDK path reaches NavDotNet.CreateNavServerHandle, whose catch block throws
    /// RunnerOutOfScopeException ("dotnet-server-interop"), making the failure loud and named.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static System.Threading.Tasks.ValueTask<bool> NavApplicationObjectBase_TryInvokeAsync(
        object? session, System.Func<System.Threading.Tasks.ValueTask>? method)
    {
        if (method == null) return new System.Threading.Tasks.ValueTask<bool>(false);
        try
        {
            method().GetAwaiter().GetResult();
            return new System.Threading.Tasks.ValueTask<bool>(true);
        }
        catch (Exception ex)
        {
            if (ex is Microsoft.Dynamics.Nav.Types.Exceptions.NavBaseException nbe && !nbe.UntrappableError)
                return new System.Threading.Tasks.ValueTask<bool>(false);
            // Same permanent-OOS trap as TryInvoke — see IsPermanentOutOfScope.
            if (IsPermanentOutOfScope(ex, out var oos))
            {
                ReportOosTrappedByTryFunction(oos!);
                return new System.Threading.Tasks.ValueTask<bool>(false);
            }
            throw;
        }
    }
}
