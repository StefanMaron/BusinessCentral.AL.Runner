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

    // NavDialogALError_NavALErrorInfo, plus the TryRouteToErrorCollection helper and its two
    // cached reflection handles, used to live here as the replacement for
    // NavDialog.ALError(Guid, NavALErrorInfo). Their Hook(...) registration was orphaned under
    // the default Cecil-only mode, so none of it ever ran, and BC's real ALError is strictly
    // more faithful than it was: the replacement returned SILENTLY for an ErrorType::Internal
    // ErrorInfo, where BC throws its masked, correlation-carrying text (#1883, corpus codeunit
    // 60758). BC's own session.ErrorCollection.Collect is also the first thing its real body
    // does, so the [ErrorBehavior(Collect)] routing this helper hand-rolled is BC's already.
}
