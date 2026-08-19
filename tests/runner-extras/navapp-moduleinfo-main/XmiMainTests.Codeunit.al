/// <summary>
/// Per-module NavApp.GetCurrentModuleInfo/GetCallerModuleInfo/GetModuleInfo.
/// A dependency's code must see ITS OWN module identity (the SPBLIC
/// CheckSupportedVersion install pattern); the bundle must see its own; the
/// caller-module lookup inside the dep must name this bundle.
/// </summary>
codeunit 61240 "XMI Main Tests"
{
    Subtype = Test;

    [Test]
    procedure DepGetCurrentModuleInfo_ReturnsDepOwnVersion()
    var
        DepApi: Codeunit "XMI Dep Api";
    begin
        if DepApi.OwnVersion() <> '25.8.43.0' then
            Error('Dep must see its OWN version 25.8.43.0, got %1.', DepApi.OwnVersion());
    end;

    [Test]
    procedure DepGetCurrentModuleInfo_ReturnsDepOwnName()
    var
        DepApi: Codeunit "XMI Dep Api";
    begin
        if DepApi.OwnName() <> 'NavAppModuleInfo Dep' then
            Error('Dep must see its OWN name, got %1.', DepApi.OwnName());
    end;

    [Test]
    procedure BundleGetCurrentModuleInfo_ReturnsBundleIdentity()
    var
        Info: ModuleInfo;
    begin
        NavApp.GetCurrentModuleInfo(Info);
        if Info.Name() <> 'NavAppModuleInfo Main' then
            Error('Bundle must see its own name, got %1.', Info.Name());
        if Format(Info.AppVersion()) <> '1.0.0.0' then
            Error('Bundle must see its own version 1.0.0.0, got %1.', Format(Info.AppVersion()));
    end;

    [Test]
    procedure DepGetCallerModuleInfo_NamesTheBundle()
    var
        DepApi: Codeunit "XMI Dep Api";
    begin
        if DepApi.CallerName() <> 'NavAppModuleInfo Main' then
            Error('Caller module inside the dep must be this bundle, got %1.', DepApi.CallerName());
    end;

    /// <summary>
    /// THE REGRESSION. GetCallerModuleInfo must name the IMMEDIATE caller's module, even
    /// when that module is the dep's own. BC's ALGetCallerModuleInfo calls
    /// GetCallingAppId(excludeCurrentMethod: true), which skips exactly ONE method scope
    /// and then breaks on the very next stack frame — it never walks past frames that
    /// happen to belong to the same app.
    ///
    /// A runner that instead returns "the nearest frame from a DIFFERENT app" answers with
    /// this bundle here. Any dep that registers data keyed on GetCallerModuleInfo().Id()
    /// then writes one row per calling app instead of one row for itself, and a later
    /// name lookup that expects a single owner reports AMBIGUOUS — an error naming the
    /// wrong problem, far from this call.
    /// </summary>
    [Test]
    procedure DepGetCallerModuleInfo_AfterOwnHop_NamesTheDepNotTheBundle()
    var
        DepApi: Codeunit "XMI Dep Api";
    begin
        if DepApi.CallerNameAfterOwnHop() <> 'NavAppModuleInfo Dep' then
            Error('Caller module across a SAME-APP hop must be the dep itself, got %1.',
                DepApi.CallerNameAfterOwnHop());
    end;

    /// <summary>
    /// The same hop by AppId — pins that the answer is the dep's real id and not an empty
    /// GUID, which is the shape that silently produces unusable rows.
    /// </summary>
    [Test]
    procedure DepGetCallerModuleInfo_AfterOwnHop_CarriesTheDepAppId()
    var
        DepApi: Codeunit "XMI Dep Api";
        EmptyId: Guid;
    begin
        if DepApi.CallerIdAfterOwnHop() = EmptyId then
            Error('Caller module id across a same-app hop must not be an empty GUID.');
        if DepApi.CallerIdAfterOwnHop() <> DepApi.OwnId() then
            Error('Caller module id across a same-app hop must equal the dep''s own id, got %1 vs %2.',
                DepApi.CallerIdAfterOwnHop(), DepApi.OwnId());
    end;

    [Test]
    procedure GetModuleInfo_ByDepAppId_ResolvesRegisteredDep()
    var
        Info: ModuleInfo;
    begin
        if not NavApp.GetModuleInfo('f6c0e4a8-7d3b-4a1c-8e5f-9b4d8c3a6f7e', Info) then
            Error('GetModuleInfo must resolve the loaded dependency by AppId.');
        if Format(Info.AppVersion()) <> '25.8.43.0' then
            Error('GetModuleInfo(depId) must carry the dep version, got %1.', Format(Info.AppVersion()));
    end;

    /// <summary>
    /// THE FIX (#1942). Before it, the source polyfill behind NavApp.GetCurrentModuleInfo
    /// was declared `void`, so the C# Roslyn compile of the emitted assembly failed with
    /// CS0023 ("operator '!' cannot be applied to operand of type 'void'") on this exact
    /// boolean-CONTEXT form -- a compile failure, not a wrong answer, which is why it went
    /// unnoticed: every existing test in this bundle used the statement form instead.
    /// Proves the boolean form now compiles, returns true, and populates the SAME bundle
    /// identity the statement-form test above already proved -- a default-constructed
    /// ModuleInfo (empty name/id, version 0.0.0.0) would fail every assertion here.
    /// </summary>
    [Test]
    procedure BundleGetCurrentModuleInfo_BooleanForm_ReturnsTrueAndBundleIdentity()
    var
        Info: ModuleInfo;
    begin
        if not NavApp.GetCurrentModuleInfo(Info) then
            Error('NavApp.GetCurrentModuleInfo must return true.');
        if Info.Name() <> 'NavAppModuleInfo Main' then
            Error('Bundle must see its own name, got %1.', Info.Name());
        if Format(Info.AppVersion()) <> '1.0.0.0' then
            Error('Bundle must see its own version 1.0.0.0, got %1.', Format(Info.AppVersion()));
        if Info.Id() <> 'a7d1f5b9-8e4c-4b2d-9f6a-0c5e9d4b7a8f' then
            Error('Bundle must see its own AppId, got %1.', Info.Id());
    end;

    /// <summary>
    /// Discriminating direction for the same fix: a broken polyfill that returns one
    /// hard-coded identity (e.g. always the bundle's) would pass the test above and fail
    /// here, or vice versa. The dep must see ITS OWN identity through the boolean form,
    /// never the consuming bundle's -- the exact per-emitted-assembly split the statement
    /// form already proves for <c>DepGetCurrentModuleInfo_ReturnsDepOwnVersion</c>.
    /// </summary>
    [Test]
    procedure DepGetCurrentModuleInfo_BooleanForm_ReturnsDepOwnIdentity()
    var
        DepApi: Codeunit "XMI Dep Api";
    begin
        if DepApi.OwnVersionBooleanForm() <> '25.8.43.0' then
            Error('Dep must see its OWN version 25.8.43.0 through the boolean form, got %1.',
                DepApi.OwnVersionBooleanForm());
        if DepApi.OwnNameBooleanForm() <> 'NavAppModuleInfo Dep' then
            Error('Dep must see its OWN name through the boolean form, got %1.', DepApi.OwnNameBooleanForm());
        if DepApi.OwnIdBooleanForm() <> 'f6c0e4a8-7d3b-4a1c-8e5f-9b4d8c3a6f7e' then
            Error('Dep must see its OWN AppId through the boolean form, got %1.', DepApi.OwnIdBooleanForm());
    end;

    /// <summary>
    /// Boolean-form coverage for GetCallerModuleInfo. Its polyfill already returned
    /// `bool` before #1942 (unlike GetCurrentModuleInfo), so this is regression cover for
    /// a value context that was never previously exercised -- not part of the fix itself.
    /// </summary>
    [Test]
    procedure DepGetCallerModuleInfo_BooleanForm_ReturnsTrueAndNamesBundle()
    var
        DepApi: Codeunit "XMI Dep Api";
    begin
        if DepApi.CallerNameBooleanForm() <> 'NavAppModuleInfo Main' then
            Error('Caller module inside the dep must be this bundle through the boolean form, got %1.',
                DepApi.CallerNameBooleanForm());
    end;
}
