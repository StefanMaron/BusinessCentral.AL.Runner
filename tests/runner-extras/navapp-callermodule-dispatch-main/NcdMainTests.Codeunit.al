/// <summary>
/// #1722 — NavApp.GetCallerModuleInfo() must name the DIRECT CALLER's module.
///
/// On the cross-app invocation path the AL emit routes the call through a
/// compiler-generated <c>Codeunit&lt;N&gt;.OnInvoke</c> dispatch wrapper that lives in the
/// CALLEE's own assembly. The runner's frame walk folded away the shim and
/// <c>*_Scope</c> frames but not that wrapper, so it accepted the callee's own
/// dispatch frame as "the immediate caller" and answered with the library's own
/// module — indistinguishable from GetCurrentModuleInfo.
///
/// Measured consequence: Microsoft's System.AI."Copilot Capability".RegisterCapability
/// keys its registration on GetCallerModuleInfo().Id(). With the library's own id
/// returned instead of the calling app's, the row lands under the wrong key, the
/// test library's later Get misses, and every subsequent call in the same codeunit
/// reports "Capability has already been registered."
/// </summary>
codeunit 64290 "NCD Main Tests"
{
    Subtype = Test;

    /// <summary>
    /// THE REGRESSION, positive direction: the id the dep observes is THIS bundle's.
    /// </summary>
    [Test]
    procedure DepGetCallerModuleInfo_AcrossApps_ReturnsTheCallingBundleId()
    var
        LibApi: Codeunit "NCD Lib Api";
        MyOwnModuleInfo: ModuleInfo;
    begin
        NavApp.GetCurrentModuleInfo(MyOwnModuleInfo);

        if LibApi.CallerModuleId() <> MyOwnModuleInfo.Id() then
            Error('GetCallerModuleInfo() inside the dep must return the DIRECT CALLER''s module id (this bundle, %1), got %2.',
                MyOwnModuleInfo.Id(), LibApi.CallerModuleId());
    end;

    /// <summary>
    /// THE REGRESSION, negative direction. This is the assertion that fails on the
    /// broken build. Kept separate from the positive one because an implementation
    /// that answers "the executing module" satisfies neither, but an implementation
    /// that merely returns a non-empty GUID would slip past a weaker check.
    /// </summary>
    [Test]
    procedure DepGetCallerModuleInfo_AcrossApps_IsNotTheDepsOwnModuleId()
    var
        LibApi: Codeunit "NCD Lib Api";
    begin
        if LibApi.CallerModuleId() = LibApi.OwnModuleId() then
            Error('GetCallerModuleInfo() inside the dep returned the dep''s OWN module id (%1) — it must name the caller, not the callee.',
                LibApi.OwnModuleId());
    end;

    /// <summary>The same claim by name, so a failure reads without resolving GUIDs.</summary>
    [Test]
    procedure DepGetCallerModuleInfo_AcrossApps_NamesTheCallingBundle()
    var
        LibApi: Codeunit "NCD Lib Api";
    begin
        if LibApi.CallerModuleName() <> 'NavAppCallerModule Dispatch Main' then
            Error('GetCallerModuleInfo() inside the dep must name this bundle, got ''%1''.',
                LibApi.CallerModuleName());
    end;

    /// <summary>
    /// Guards the fix from overshooting: folding the dispatch wrapper must not also
    /// fold the callee's real frame, which would make the dep see its own CURRENT
    /// module wrongly. GetCurrentModuleInfo inside the dep still answers the dep.
    /// </summary>
    [Test]
    procedure DepGetCurrentModuleInfo_AcrossApps_StillNamesTheDep()
    var
        LibApi: Codeunit "NCD Lib Api";
    begin
        if LibApi.OwnModuleName() <> 'NavAppCallerModule Dispatch Dep' then
            Error('GetCurrentModuleInfo() inside the dep must still name the dep itself, got ''%1''.',
                LibApi.OwnModuleName());
    end;
}
