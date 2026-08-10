/// <summary>
/// Library half of the #1722 reproducer. Mirrors the idiom Microsoft's
/// System.AI."Copilot Capability".RegisterCapability uses: a SHARED library asks
/// NavApp.GetCallerModuleInfo() so it can register state keyed on the module of
/// whoever called into it — never on its own module.
/// </summary>
codeunit 64280 "NCD Lib Api"
{
    Access = Public;

    /// <summary>AppId of the module that called INTO this library procedure.</summary>
    procedure CallerModuleId(): Guid
    var
        CallerModuleInfo: ModuleInfo;
    begin
        NavApp.GetCallerModuleInfo(CallerModuleInfo);
        exit(CallerModuleInfo.Id());
    end;

    /// <summary>Name of the module that called INTO this library procedure.</summary>
    procedure CallerModuleName(): Text
    var
        CallerModuleInfo: ModuleInfo;
    begin
        NavApp.GetCallerModuleInfo(CallerModuleInfo);
        exit(CallerModuleInfo.Name());
    end;

    /// <summary>This library's OWN module id, for contrast with the caller's.</summary>
    procedure OwnModuleId(): Guid
    var
        CurrentModuleInfo: ModuleInfo;
    begin
        NavApp.GetCurrentModuleInfo(CurrentModuleInfo);
        exit(CurrentModuleInfo.Id());
    end;

    /// <summary>This library's OWN module name.</summary>
    procedure OwnModuleName(): Text
    var
        CurrentModuleInfo: ModuleInfo;
    begin
        NavApp.GetCurrentModuleInfo(CurrentModuleInfo);
        exit(CurrentModuleInfo.Name());
    end;
}
