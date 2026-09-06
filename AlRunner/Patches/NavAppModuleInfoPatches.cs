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
    // WHY: this is the BY-ID overload — AL's `NavApp.GetModuleInfo(appId, info)` — and it is
    // the one the two patches above do NOT cover. Real BC resolves the id against
    // NavCurrentThread.TryResolveAppGroup().OrderedAppMetadata; the runner has no app group,
    // so TryResolveAppGroup answers null, no metadata matches ANY id, and BC's own
    // not-found arm raises
    //
    //     NavAppException: No installed extension was found with ID '<guid>'.
    //
    // for every app — including apps the runner has loaded, and including the System
    // Application's own id. So `NavApp.VersionInstalled` / `NavApp.GetModuleInfo` could not
    // answer "yes" about anything (#2961). That is not a missing surface; it is the runner
    // failing to report the closure it demonstrably holds.
    //
    // WHAT THIS DOES: answers from BcRuntime.RegisteredModules() — the same loaded-app
    // closure RecordPatches.EnsurePublishedApplicationRowsSeeded writes into Published
    // Application (#2963), so the two views cannot disagree. Reconstruction of state the
    // runner holds, not a fake.
    //
    // BC's NOT-FOUND SEMANTICS ARE PRESERVED EXACTLY. An id the runner has not loaded is
    // genuinely not installed here, so the TrapError arm still returns false with a null
    // info, and the raising arm still throws NavAppException with BC's own message. A patch
    // that answered "installed" for every id would be the silent-fake this repo forbids.
    //
    // PackageId comes from AppPackageIdentity — the SAME deterministic per-app GUID stamped
    // onto this app's Published Application row and its AllObj rows — so AL that reads
    // ModuleInfo.PackageId and then looks the app up by package id finds it. The two
    // stack-walk patches above still pass appId there; they predate AppPackageIdentity and
    // are left alone rather than changed blind (#3072-adjacent, not measured here).
    //
    // DataVersion is reported as the app version, matching the two patches above. Real BC
    // computes GetDataVersionForInstall/GetDataVersionForUpgrade off publish-time state the
    // runner does not have.
    //
    // The moduleId == Guid.Empty branch is NOT implemented: BC answers it from
    // NavGlobal.AppDatabase.SqlDatabaseProperties.ApplicationFamily, which the runner has no
    // truthful source for, and inventing a family string would be a silent wrong answer.
    // It refuses loudly instead (.claude/rules/loud-failures.md). Today that branch NREs on
    // the skeleton, so refusing is strictly more informative than the status quo.
    public static bool ALNavApp_GetModuleInfo(
        Microsoft.Dynamics.Nav.Types.DataError errorLevel,
        System.Guid moduleId,
        Microsoft.Dynamics.Nav.Runtime.ByRef<Microsoft.Dynamics.Nav.Runtime.NavModuleInfo> info)
    {
        if (moduleId == System.Guid.Empty)
            throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                "NavApp.GetModuleInfo(Guid.Empty)",
                "not-yet-implemented",
                "docs/scope.md");

        foreach (var m in AlRunner.BcRuntime.RegisteredModules())
        {
            if (m.AppId != moduleId) continue;
            var navVersion = new Microsoft.Dynamics.Nav.Runtime.NavVersion(m.Version);
            var emptyDeps = Microsoft.Dynamics.Nav.Runtime.NavList<Microsoft.Dynamics.Nav.Runtime.NavModuleDependencyInfo>.Default;
            info.Value = new Microsoft.Dynamics.Nav.Runtime.NavModuleInfo(
                m.AppId, m.Name, m.Publisher, navVersion, navVersion, emptyDeps,
                AlRunner.Infrastructure.AppPackageIdentity.PackageIdFor(m.AppId));
            return true;
        }

        // Not loaded => not installed. BC's own two arms, verbatim in behaviour.
        info.Value = null;
        if (errorLevel == Microsoft.Dynamics.Nav.Types.DataError.TrapError)
            return false;
        throw new Microsoft.Dynamics.Nav.Types.Exceptions.NavAppException(
            Microsoft.Dynamics.Nav.Diagnostic.PrivacyClassification.SystemMetadata,
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                "No installed extension was found with ID '{0}'.", moduleId));
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
