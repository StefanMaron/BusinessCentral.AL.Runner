/// <summary>
/// Reports this DEPENDENCY app's own module identity — the SPBLIC
/// CheckSupportedVersion pattern (NavApp.GetCurrentModuleInfo inside a dep must
/// see the dep's version, not the consuming bundle's).
/// </summary>
codeunit 61230 "XMI Dep Api"
{
    procedure OwnVersion(): Text
    var
        Info: ModuleInfo;
    begin
        NavApp.GetCurrentModuleInfo(Info);
        exit(Format(Info.AppVersion()));
    end;

    procedure OwnName(): Text
    var
        Info: ModuleInfo;
    begin
        NavApp.GetCurrentModuleInfo(Info);
        exit(Info.Name());
    end;

    procedure CallerName(): Text
    var
        Info: ModuleInfo;
    begin
        NavApp.GetCallerModuleInfo(Info);
        exit(Info.Name());
    end;
}
