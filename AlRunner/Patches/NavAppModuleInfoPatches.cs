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

    // Cecil patch target: static bool ALNavApp.ALGetModuleInfo(DataError, Guid, ByRef<NavModuleInfo>)
    //
    // BC's own body resolves the id against
    // NavCurrentThread.TryResolveAppGroup().OrderedAppMetadata, which is empty on the skeleton,
    // so EVERY id missed and — because AL emits a bare `NavApp.GetModuleInfo(id, info)` statement
    // with DataError.ThrowError — the miss threw "No installed extension was found with ID '<id>'"
    // for apps the runner has loaded, including the System Application itself. Measured on BC 28.1
    // from Codeunit1809 "Assisted Setup Installation".OnInstallAppPerCompany, which asks
    // VersionInstalled(its own app id) (issue #2961).
    //
    // Source-compiled AL never hit this: BcAssembler rewrites its ALNavApp.ALGetModuleInfo call
    // sites to NavRuntimeHelpersShim.ALNavApp_GetModuleInfo. Precompiled apps call the real Ncl
    // method, so they need this Cecil patch — the same asymmetry ALGetCurrentModuleInfo and
    // ALGetCallerModuleInfo above were patched for.
    //
    // Faithfulness (.claude/rules/loud-failures.md): the answer comes from the runner's own
    // registry of loaded modules, which is the closure BC's OrderedAppMetadata would describe,
    // so a hit is a real answer rather than a default. A genuine miss reproduces BC's own
    // behaviour instead of returning a default that lets the caller proceed on a wrong answer:
    // DataError.TrapError yields `false` with a null ModuleInfo (the AL `if not
    // NavApp.GetModuleInfo(...)` form), anything else throws.
    //
    // DataVersion deliberately equals Version, and that is BC's own answer under the runner's
    // session state rather than a shortcut: BC computes
    // `GetDataVersionForInstall(app) ?? GetDataVersionForUpgrade(app) ?? app.Version`, and both
    // helpers are gated on NavCurrentThread.Session.AppInstallationContext / AppUpgradeContext
    // targeting this very app. The runner never sets either, so BC's expression falls through to
    // app.Version. The sibling helpers above already answer the same way.
    public static bool ALNavApp_GetModuleInfo(
        Microsoft.Dynamics.Nav.Types.DataError errorLevel,
        Guid moduleId,
        Microsoft.Dynamics.Nav.Runtime.ByRef<Microsoft.Dynamics.Nav.Runtime.NavModuleInfo> info)
    {
        var found = AlRunner.BcRuntime.TryGetModuleInfoByAppId(moduleId);
        if (found == null)
        {
            info.Value = null!;
            if (errorLevel == Microsoft.Dynamics.Nav.Types.DataError.TrapError) return false;
            throw NoInstalledMatch(moduleId);
        }
        var (appId, name, publisher, version) = found.Value;
        var navVersion = new Microsoft.Dynamics.Nav.Runtime.NavVersion(version);
        var emptyDeps = Microsoft.Dynamics.Nav.Runtime.NavList<Microsoft.Dynamics.Nav.Runtime.NavModuleDependencyInfo>.Default;
        info.Value = new Microsoft.Dynamics.Nav.Runtime.NavModuleInfo(
            appId, name, publisher, navVersion, navVersion, emptyDeps, appId);
        return true;
    }

    /// <summary>
    /// BC's own exception for an id that resolves to no installed app, so AL `asserterror` /
    /// `GetLastErrorText` see what a service tier would show. The message text is copied from
    /// BC 28.1's own output for this case, observed while diagnosing #2961 — it is not invented,
    /// and if the type cannot be resolved the runner says so rather than inventing an answer.
    /// </summary>
    private static Exception NoInstalledMatch(Guid moduleId)
    {
        var message = string.Format(System.Globalization.CultureInfo.CurrentCulture,
            "No installed extension was found with ID '{0}'.", moduleId);
        try
        {
            return new Microsoft.Dynamics.Nav.Types.Exceptions.NavAppException(
                Microsoft.Dynamics.Nav.Diagnostic.PrivacyClassification.SystemMetadata, message);
        }
        catch (Exception ex)
        {
            return new AlRunner.Infrastructure.RunnerOutOfScopeException(
                "NavApp.GetModuleInfo",
                $"could not construct BC's NavAppException for an unresolved module id " +
                $"({ex.GetType().Name}: {ex.Message}) — BC/runtime shape changed. " +
                $"The underlying condition is: {message}");
        }
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
