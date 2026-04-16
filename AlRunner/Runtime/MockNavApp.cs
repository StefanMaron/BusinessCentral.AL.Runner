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
    /// NavApp.GetResourceAsText(ResourceName [, TextEncoding]) : Text — function returning
    /// the resource as a string. No .app in standalone mode; returns empty string.
    /// BC emits: ALNavApp.ALGetResourceAsText(DataError, NavText [, object?]) → NavText
    /// </summary>
    public static NavText ALGetResourceAsText(DataError errorLevel, NavText resourceName)
        => NavText.Empty;

    public static NavText ALGetResourceAsText(DataError errorLevel, NavText resourceName, object? encoding)
        => NavText.Empty;

    /// <summary>
    /// NavApp.GetResourceAsJson(ResourceName [, TextEncoding]) : JsonToken — function
    /// returning the resource as a JSON token. No .app in standalone mode; returns default.
    /// BC emits: ALNavApp.ALGetResourceAsJson(DataError, NavText [, object?]) → NavJsonToken
    /// </summary>
    public static NavJsonToken ALGetResourceAsJson(DataError errorLevel, NavText resourceName)
        => default;

    public static NavJsonToken ALGetResourceAsJson(DataError errorLevel, NavText resourceName, object? encoding)
        => default;

    /// <summary>
    /// NavApp.ListResources([ResourceType]) : List of [Text] — function returning
    /// resource names. No .app in standalone mode; returns empty list.
    /// BC emits: ALNavApp.ALListResources(DataError [, NavText]) → NavList&lt;NavText&gt;
    /// </summary>
    public static NavList<NavText> ALListResources(DataError errorLevel)
        => NavList<NavText>.Default;

    public static NavList<NavText> ALListResources(DataError errorLevel, NavText resourceType)
        => NavList<NavText>.Default;
}
