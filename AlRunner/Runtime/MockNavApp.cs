using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Runtime;

/// <summary>
/// Stub for <c>ALNavApp</c>. The real BC type's
/// <c>ALGetModuleInfo</c> / <c>ALGetCurrentModuleInfo</c> /
/// <c>ALGetCallerModuleInfo</c> reach into
/// <c>Microsoft.Dynamics.Nav.CodeAnalysis</c>, which isn't shipped
/// with al-runner — any AL call path that touches NavApp metadata
/// crashes with "Could not load file or assembly ..." under
/// standalone mode.
///
/// MockNavApp returns false for every lookup (no module metadata is
/// available without a service tier), leaving the ByRef'd
/// <see cref="NavModuleInfo"/> at its default. Callers are expected
/// to treat the false return as "not found" and fall back to a
/// placeholder, matching BC's own contract.
/// </summary>
public static class MockNavApp
{
    public static bool ALGetModuleInfo(DataError errorLevel, Guid moduleId, ByRef<NavModuleInfo> info)
    {
        // Intentionally don't touch the ByRef — the caller's local
        // ModuleInfo stays at its zero state, which BC handles as
        // "no module info". Setting a real value would require
        // metadata we don't have.
        return false;
    }

    public static bool ALGetCurrentModuleInfo(DataError errorLevel, ByRef<NavModuleInfo> info)
    {
        return false;
    }

    public static bool ALGetCallerModuleInfo(DataError errorLevel, ByRef<NavModuleInfo> info)
    {
        return false;
    }

    public static NavList<NavModuleInfo> ALGetCallerCallstackModuleInfos()
    {
        return NavList<NavModuleInfo>.Default;
    }

    /// <summary>
    /// Returns false — no installation lifecycle exists in standalone mode.
    /// BC emits this as ALNavAppIsInstalling() (type-prefixed method name).
    /// BC's real implementation returns true only during app installation on a service tier.
    /// </summary>
    public static bool ALNavAppIsInstalling() => false;

    /// <summary>
    /// Returns false — no license enforcement is applied in standalone mode.
    /// BC emits this as ALIsUnlicensed(DataError).
    /// BC's real implementation returns true when the app has no valid license.
    /// </summary>
    public static bool ALIsUnlicensed(DataError errorLevel) => false;

    /// <summary>
    /// Returns true — the runner grants full entitlement so all code paths are reachable.
    /// BC emits this as ALIsEntitled(DataError, NavText [, Guid]).
    /// </summary>
    public static bool ALIsEntitled(DataError errorLevel, NavText id) => true;

    /// <summary>
    /// Overload with optional AppId: IsEntitled(Id: Text[250]; AppId: Guid).
    /// </summary>
    public static bool ALIsEntitled(DataError errorLevel, NavText id, Guid appId) => true;
}
