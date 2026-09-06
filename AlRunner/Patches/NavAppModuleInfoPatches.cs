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
    /// <summary>
    /// The ONE place a <c>NavModuleInfo</c> is constructed for an app the runner has
    /// identified. Every module-info entry point goes through it — the two Cecil patches in
    /// this file that serve PRECOMPILED AL, the by-id patch below, and the two source-compiled
    /// polyfills in <c>BcAssembler.NavRuntimeHelpersShim</c>.
    ///
    /// <para>WHY IT HAS TO BE ONE PLACE. Real BC has exactly one implementation:
    /// <c>ALGetCurrentModuleInfo</c> and <c>ALGetCallerModuleInfo</c> are each a two-line
    /// forward into <c>ALGetModuleInfo</c> (decompiled from bc281, and the same shape on every
    /// cached version), which fills <c>PackageId</c> from
    /// <c>navAppRuntimeMetadata.PackageId.Value</c> for all three callers. So in BC, AL asking
    /// about one app three different ways gets three identical answers, and any runner in
    /// which it does not is diverging from AL-observable behaviour.</para>
    ///
    /// <para>The runner patches the three entry points independently, so keeping them
    /// consistent is a property of this code and nothing else. Before #2961 all five sites
    /// passed <c>appId</c> as the <c>PackageId</c> — wrong, but at least uniformly wrong.
    /// Fixing only the by-id one would have made <c>GetModuleInfo(x).PackageId</c> and
    /// <c>GetCurrentModuleInfo().PackageId</c> disagree for the same app x, which is worse
    /// than either answer on its own. Routing them all through here is what stops that
    /// recurring the next time one of the five is touched.</para>
    ///
    /// <para>PACKAGE ID. <see cref="AlRunner.Infrastructure.AppPackageIdentity.PackageIdFor"/>
    /// — the SAME deterministic per-app GUID the runner stamps onto that app's Published
    /// Application row (#2963) and onto its AllObj rows (#3066), so AL that reads
    /// <c>ModuleInfo.PackageId</c> and then looks the app up by package id finds it. It is
    /// <c>Guid.Empty</c> in / <c>Guid.Empty</c> out, so an unidentified caller still reports a
    /// blank package id rather than a derived one claiming to be some app.</para>
    ///
    /// <para>NOT FAITHFUL, AND SAID SO: <c>DataVersion</c> is reported as the app version. Real
    /// BC computes it from <c>GetDataVersionForInstall</c> / <c>GetDataVersionForUpgrade</c>,
    /// which read publish-time state the runner has no source for. The dependency list is
    /// likewise empty where BC projects the app's real dependency set; nothing measured here
    /// reads it, and inventing entries would be worse than an empty list a caller can see is
    /// empty.</para>
    /// </summary>
    public static Microsoft.Dynamics.Nav.Runtime.NavModuleInfo MakeModuleInfo(
        System.Guid appId, string name, string publisher, string version)
    {
        var navVersion = new Microsoft.Dynamics.Nav.Runtime.NavVersion(version);
        var emptyDeps = Microsoft.Dynamics.Nav.Runtime.NavList<Microsoft.Dynamics.Nav.Runtime.NavModuleDependencyInfo>.Default;
        return new Microsoft.Dynamics.Nav.Runtime.NavModuleInfo(
            appId, name, publisher, navVersion, navVersion, emptyDeps,
            AlRunner.Infrastructure.AppPackageIdentity.PackageIdFor(appId));
    }

    // Cecil patch target: static bool ALNavApp.ALGetCurrentModuleInfo(DataError, ByRef<NavModuleInfo>)
    // Called from precompiled deps where the BcAssembler polyfill redirect does not apply.
    // Uses a stack-walk to identify the first registered AL assembly on the call stack —
    // that is the precompiled dep whose AL code invoked NavApp.GetCurrentModuleInfo.
    public static bool ALNavApp_GetCurrentModuleInfo(
        Microsoft.Dynamics.Nav.Types.DataError errorLevel,
        Microsoft.Dynamics.Nav.Runtime.ByRef<Microsoft.Dynamics.Nav.Runtime.NavModuleInfo> info)
    {
        var (appId, name, publisher, version) = AlRunner.BcRuntime.GetCurrentModuleFromCallStack();
        info.Value = MakeModuleInfo(appId, name, publisher, version);
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
    // WHAT THIS DOES: answers from BcRuntime.TryGetModuleInfoByAppId — the SAME lookup the
    // source-compiled polyfill in BcAssembler already used, and over the same loaded-app
    // closure RecordPatches.EnsurePublishedApplicationRowsSeeded writes into Published
    // Application (#2963), so no two views of "which apps are installed" can disagree.
    // Reconstruction of state the runner holds, not a fake.
    //
    // The source-compiled polyfill now CALLS this method rather than carrying its own copy
    // (BcAssembler.NavRuntimeHelpersShim.ALNavApp_GetModuleInfo). It had two divergences
    // worth naming, because a single implementation is the only way they stay fixed:
    // it returned false on not-found whatever the DataError was, so the STATEMENT form of
    // NavApp.GetModuleInfo silently populated nothing instead of raising the way BC does;
    // and it reported appId as the PackageId, which is not the value the runner stamps on
    // that app's Published Application row or its AllObj rows.
    //
    // BC's NOT-FOUND SEMANTICS ARE PRESERVED EXACTLY. An id the runner has not loaded is
    // genuinely not installed here, so the TrapError arm still returns false with a null
    // info, and the raising arm still throws NavAppException with BC's own message. A patch
    // that answered "installed" for every id would be the silent-fake this repo forbids.
    //
    // PackageId, DataVersion and the dependency list all come from MakeModuleInfo above,
    // shared with the two stack-walk patches and the two source-compiled polyfills, so the
    // three AL-visible ways of asking about one app cannot disagree — which is exactly what
    // real BC guarantees by having ALGetCurrentModuleInfo and ALGetCallerModuleInfo forward
    // into THIS method. See MakeModuleInfo's header for what is and is not faithful in it.
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

        var found = AlRunner.BcRuntime.TryGetModuleInfoByAppId(moduleId);
        if (found != null)
        {
            var (appId, name, publisher, version) = found.Value;
            info.Value = MakeModuleInfo(appId, name, publisher, version);
            return true;
        }

        // Not loaded => not installed. Same CONTROL FLOW as BC's two arms: a TrapError caller
        // gets false with a null info, a raising caller gets a NavAppException of the same type
        // naming the same id.
        //
        // ONE DIVERGENCE, and it is in the message TEXT rather than the behaviour. BC formats
        // Lang.NavApp_NoInstalledMatchFound under CultureInfo.CurrentCulture — a localized
        // resource — and this inlines the English literal. Under the invariant culture the
        // runner executes in the two are the same string, and the runner-extras test that
        // matches on it asserts the English fragment, so nothing observes the difference today.
        // It would become observable the moment the runner ran under a localized culture with
        // BC's own resources loaded, which is why it is recorded here rather than left to be
        // rediscovered. Reading the resource instead would couple this patch to a Lang class
        // whose accessibility is not part of the DLL contract we may rely on.
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
        info.Value = MakeModuleInfo(appId, name, publisher, version);
        return true;
    }
}
