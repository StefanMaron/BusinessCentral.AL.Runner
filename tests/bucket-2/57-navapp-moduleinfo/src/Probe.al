codeunit 56570 "NA Probe"
{
    procedure TryUnknown(): Text
    var
        Info: ModuleInfo;
        Found: Boolean;
    begin
        Found := NavApp.GetModuleInfo('00000000-0000-0000-0000-000000000000', Info);
        if Found then
            exit(Info.Name);
        exit('<unknown>');
    end;

    procedure ReadsNamePropertyWhenMissing(): Text
    var
        Info: ModuleInfo;
    begin
        // Default ModuleInfo has empty strings — reading Name must not throw.
        exit(Info.Name);
    end;
}
