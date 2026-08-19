// TelemetryPatches — replacements for telemetry / diagnostic / dialog APIs that NRE
// because the underlying EventSource singletons and DiagnosticsResolver instances are
// uninitialised in headless mode.
//
// We either return null/no-op (telemetry is not needed) or convert AL errors into
// throwable exceptions so asserterror still traps them.
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AlRunner;

public static partial class BcRuntime
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object? NavServerEventSource_get_Log() => _skeletonNavServerEventSource;

    /// <summary>
    /// No-op for NavServerEventSource.WritePermissionUncheckedEvent — instance method with 10 args
    /// (4 strings + 6 ints). The replacement is static so first arg is the receiver.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavServerEventSource_WritePermissionUncheckedEvent(
        object self,
        string serverInstanceName, string navTenantId, string environmentName, string environmentType,
        int sessionId, int objectType, int objectId, int permissions, int callingObjectType, int callingObjectId)
    { }

    /// <summary>
    /// Replacement for NavDialog.ALError(Guid automationId, NavALErrorInfo errorInfo).
    /// On Linux x86-64, Guid (16 bytes) occupies two register slots, so the actual
    /// parameters received are: a = Guid-lo64, b = Guid-hi64, errorInfo = NavALErrorInfo.
    /// The real body NREs through diagnostics infrastructure; we construct NavNCLDialogException
    /// directly from the error message so asserterror traps it correctly.
    /// Note: the 3-arg overload ALError(NavSession, Guid, NavALErrorInfo) is hooked to NoOp4
    /// (session + two Guid halves + errorInfo) since it is only called from ALLogInternalError
    /// which we already suppress.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavDialogALError_NavALErrorInfo(object? a, object? b, object? errorInfo)
    {
        string msg = string.Empty;
        if (errorInfo != null)
        {
            try
            {
                var ei = (Microsoft.Dynamics.Nav.Runtime.NavALErrorInfo)errorInfo;
                if (ei.ALErrorType == Microsoft.Dynamics.Nav.Types.ALErrorType.Internal)
                    return;
                msg = ei.ALMessage ?? string.Empty;
            }
            catch
            {
                try
                {
                    var msgProp = errorInfo.GetType().GetProperty("ALMessage", BindingFlags.Public | BindingFlags.Instance);
                    msg = msgProp?.GetValue(errorInfo) as string ?? string.Empty;
                }
                catch { }
            }
        }

        // If we're inside a [ErrorBehavior(Collect)] scope, route the error into
        // session.ErrorCollection rather than throwing. This matches the real NavDialog.ALError
        // semantics (line 34565 of decompile) where Collect returns true and ALError returns silently.
        // Without this, AL tests using [ErrorBehavior(Collect)] see Errors.Count = 0 because the
        // throw bypasses the collect-list. See HANDOFF §6 row #4.
        if (errorInfo != null && TryRouteToErrorCollection(errorInfo))
            return;

        if (_navNCLDialogExceptionType != null)
        {
            var exc = Activator.CreateInstance(_navNCLDialogExceptionType, msg) as Exception
                ?? new Exception(msg);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(exc);
        }
        throw new Exception(msg.Length > 0 ? msg : "AL error");
    }

    private static System.Reflection.MethodInfo? _mErrorCollectionCollect;
    private static System.Reflection.FieldInfo? _fSessionErrorCollection;
    /// <summary>
    /// Try `_skeletonSession.ErrorCollection.Collect(errorInfo, out _)`. Returns true on a
    /// successful collect (i.e. error.ALCollectible &amp;&amp; collectedErrors != null — meaning a
    /// StartCollecting() scope is active). Returning true tells the caller to suppress the throw.
    /// </summary>
    private static bool TryRouteToErrorCollection(object errorInfo)
    {
        if (_skeletonSession == null) return false;
        if (_fSessionErrorCollection == null)
            _fSessionErrorCollection = _skeletonSession.GetType().GetField("<ErrorCollection>k__BackingField",
                BindingFlags.NonPublic | BindingFlags.Instance);
        var ec = _fSessionErrorCollection?.GetValue(_skeletonSession);
        if (ec == null) return false;
        if (_mErrorCollectionCollect == null)
            _mErrorCollectionCollect = ec.GetType().GetMethod("Collect",
                BindingFlags.Public | BindingFlags.Instance);
        if (_mErrorCollectionCollect == null) return false;
        var args = new object?[] { errorInfo, null };
        try { return (bool)_mErrorCollectionCollect.Invoke(ec, args)!; }
        catch { return false; }
    }
}
