// MiscPatches — small replacements that don't fit a larger concern bucket.
//
// ALSession (session-lifecycle helpers) and NCLEnumMetadata (codeunit enum lookup)
// each have one tiny replacement; rather than spawn a file per area we keep them here.
using System.Runtime.CompilerServices;

namespace AlRunner;

public static partial class BcRuntime
{
    /// <summary>
    /// Replacement for ALSession.GetALCurrentClientType(NavSession).
    /// The real body switches on session.ClientConnectionType which NREs on the skeleton session.
    /// Returns Background as a safe default matching headless/service-tier-less execution.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Microsoft.Dynamics.Nav.Types.NavClientType ALSession_GetALCurrentClientType(
        object? session)
        => Microsoft.Dynamics.Nav.Types.NavClientType.Background;

    /// <summary>
    /// Replacement for all ALSession.ALStopSessionAsync overloads.
    /// The async body NREs via session.Diagnostics on the skeleton. Return false (not stopped).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static System.Threading.Tasks.ValueTask<bool> ALSession_StopSessionAsync(
        object? a, object? b, object? c, object? d)
    {
        return new System.Threading.Tasks.ValueTask<bool>(false);
    }

    // ALSystemErrorHandling.get_AL{GetLastErrorText,GetLastErrorCode,GetLastErrorCallStack} and
    // ALClearLastError replacements (ALSystemErrorHandling_get_ALGetLastErrorText/Code/CallStack,
    // ALSystemErrorHandling_ALClearLastError) used to live here, backing an orphaned JmpHook
    // registration in BcRuntime.cs (JmpHook disabled by default, so BC's real bodies ran anyway).
    // Deleted along with the registration — see the comment in BcRuntime.cs's ApplyAllPatches
    // for the empirical evidence (#1883 follow-up).
}
