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
}
