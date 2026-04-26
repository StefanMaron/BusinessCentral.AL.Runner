/// Helper codeunit exercising StrMenu — the top-level builtin that pops a menu
/// and returns the 1-based index of the selected option (or 0 on cancel).
codeunit 59750 "SM Src"
{
    procedure Pick(options: Text): Integer
    begin
        exit(StrMenu(options));
    end;

    procedure PickWithDefault(options: Text; defaultNo: Integer): Integer
    begin
        exit(StrMenu(options, defaultNo));
    end;

    procedure PickWithDefaultAndCaption(options: Text; defaultNo: Integer; caption: Text): Integer
    begin
        exit(StrMenu(options, defaultNo, caption));
    end;
}
