// NavAppModuleInfoPatches — Cecil patch helpers for ALNavApp.ALGetCurrentModuleInfo
// and ALNavApp.ALGetCallerModuleInfo in Microsoft.Dynamics.Nav.Ncl.dll.
//
// WHY: Both methods access NavTenant.get_Database (skeleton has no real database),
// causing NREs when called from PRECOMPILED deps (SystemApp, ISV DLLs, etc.).
// Source-compiled deps are already safe: BcAssembler's polyfill redirect
// replaces their call-sites with NavRuntimeHelpersShim.ALNavApp_Get*ModuleInfo,
// which uses Assembly.GetExecutingAssembly() — correct because the shim is
// compiled INTO each dep assembly.
//
// The Cecil patch (NclCecilRewrite.cs) replaces both method bodies in Ncl.dll
// to call these helpers instead. The helpers use a stack-walk to find the
// calling registered AL assembly, matching real BC's executing-module semantics.
//
// RED→GREEN: Pageworks Codeunit50364 tests 2-7 ("Capability has already been
// registered") — caused by CopilotCapability.RegisterCapability (SystemApp
// precompiled) calling ALGetCallerModuleInfo and receiving Guid.Empty instead
// of the System App Test Library AppId, so the CopilotSettings.Rename key miss
// left the row at Guid.Empty, and subsequent tests found it at Guid.Empty and
// re-errored on IsCapabilityRegistered.

namespace AlRunner.Patches;

public static class NavAppModuleInfoPatches
{
    // Cecil patch target: static bool ALNavApp.ALGetCurrentModuleInfo(DataError, ByRef<NavModuleInfo>)
    // Called from precompiled deps where the BcAssembler polyfill redirect does not apply.
    // Uses a stack-walk to identify the first registered AL assembly on the call stack —
    // that is the precompiled dep whose AL code invoked NavApp.GetCurrentModuleInfo.
    public static bool ALNavApp_GetCurrentModuleInfo(
        Microsoft.Dynamics.Nav.Types.DataError errorLevel,
        Microsoft.Dynamics.Nav.Runtime.ByRef<Microsoft.Dynamics.Nav.Runtime.NavModuleInfo> info)
    {
        var (appId, name, publisher, version) = AlRunner.BcRuntime.GetCurrentModuleFromCallStack();
        var navVersion = new Microsoft.Dynamics.Nav.Runtime.NavVersion(version);
        var emptyDeps = Microsoft.Dynamics.Nav.Runtime.NavList<Microsoft.Dynamics.Nav.Runtime.NavModuleDependencyInfo>.Default;
        info.Value = new Microsoft.Dynamics.Nav.Runtime.NavModuleInfo(
            appId, name, publisher, navVersion, navVersion, emptyDeps, appId);
        return true;
    }

    // Cecil patch target: static bool ALNavApp.ALGetCallerModuleInfo(DataError, ByRef<NavModuleInfo>)
    // Uses a stack-walk to identify the "caller" of the current AL module:
    // finds the first registered AL assembly (the precompiled dep = "self"), then
    // returns the first DIFFERENT registered assembly above it (the true "caller").
    // Faithful to real BC semantics where GetCallerModuleInfo returns the module
    // of the nearest call-stack frame from a different AL application.
    public static bool ALNavApp_GetCallerModuleInfo(
        Microsoft.Dynamics.Nav.Types.DataError errorLevel,
        Microsoft.Dynamics.Nav.Runtime.ByRef<Microsoft.Dynamics.Nav.Runtime.NavModuleInfo> info)
    {
        var (appId, name, publisher, version) = AlRunner.BcRuntime.GetCallerModuleFromCallStack();
        var navVersion = new Microsoft.Dynamics.Nav.Runtime.NavVersion(version);
        var emptyDeps = Microsoft.Dynamics.Nav.Runtime.NavList<Microsoft.Dynamics.Nav.Runtime.NavModuleDependencyInfo>.Default;
        info.Value = new Microsoft.Dynamics.Nav.Runtime.NavModuleInfo(
            appId, name, publisher, navVersion, navVersion, emptyDeps, appId);
        return true;
    }
}
