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

    // Cached reflection for skeleton-session error access.
    private static System.Reflection.PropertyInfo? _pSessGetLastErrorText;
    private static System.Reflection.PropertyInfo? _pSessGetLastErrorCode;
    private static System.Reflection.MethodInfo? _mSessGetLastErrorCallstack;
    private static System.Reflection.MethodInfo? _mSessClearLastError;

    /// <summary>
    /// Replacement for ALSystemErrorHandling.get_ALGetLastErrorText.
    /// Real getter goes through NavCurrentThread.Session which is null on the
    /// skeleton (no thread-local session installed) and NREs. Read directly from
    /// the skeleton NavSession's `GetLastErrorText` internal property — that
    /// property is null-safe (returns string.Empty when lastException is null).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string ALSystemErrorHandling_get_ALGetLastErrorText()
    {
        if (_skeletonSession == null) return string.Empty;
        if (_pSessGetLastErrorText == null)
            _pSessGetLastErrorText = _skeletonSession.GetType().GetProperty("GetLastErrorText",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var v = _pSessGetLastErrorText?.GetValue(_skeletonSession) as string;
        return v ?? string.Empty;
    }

    /// <summary>
    /// Replacement for ALSystemErrorHandling.get_ALGetLastErrorCode. Same shape as
    /// ALGetLastErrorText — read from skeleton session's internal property.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string ALSystemErrorHandling_get_ALGetLastErrorCode()
    {
        if (_skeletonSession == null) return string.Empty;
        if (_pSessGetLastErrorCode == null)
            _pSessGetLastErrorCode = _skeletonSession.GetType().GetProperty("GetLastErrorCode",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var v = _pSessGetLastErrorCode?.GetValue(_skeletonSession) as string;
        return v ?? string.Empty;
    }

    /// <summary>
    /// Replacement for ALSystemErrorHandling.get_ALGetLastErrorCallStack.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string ALSystemErrorHandling_get_ALGetLastErrorCallStack()
    {
        // Prefer the AL call stack captured by AlCallStackCapture (FCE-based, accurate frames).
        var captured = AlRunner.Infrastructure.AlCallStackCapture.GetCaptured();
        if (!string.IsNullOrEmpty(captured)) return captured;

        // Fallback: try the native NavSession.GetLastErrorCallstack method.
        if (_skeletonSession == null) return string.Empty;
        if (_mSessGetLastErrorCallstack == null)
            _mSessGetLastErrorCallstack = _skeletonSession.GetType().GetMethod("GetLastErrorCallstack",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                null, new[] { typeof(string) }, null);
        try
        {
            var v = _mSessGetLastErrorCallstack?.Invoke(_skeletonSession, new object[] { "\\" }) as string;
            return v ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Replacement for ALSystemErrorHandling.ALClearLastError — clears skeleton session.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ALSystemErrorHandling_ALClearLastError()
    {
        if (_skeletonSession == null) return;
        if (_mSessClearLastError == null)
            _mSessClearLastError = _skeletonSession.GetType().GetMethod("ClearLastError",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                null, Type.EmptyTypes, null);
        try { _mSessClearLastError?.Invoke(_skeletonSession, null); } catch { }
    }
}
