/// Helper codeunit exercising NavApp.IsInstalling(), NavApp.IsUnlicensed(), NavApp.IsEntitled().
/// In standalone mode: IsInstalling → false, IsUnlicensed → false, IsEntitled → true.
codeunit 85000 "NAS Src"
{
    procedure GetIsInstalling(): Boolean
    begin
        exit(NavApp.IsInstalling());
    end;

    procedure GetIsUnlicensed(): Boolean
    begin
        exit(NavApp.IsUnlicensed());
    end;

    procedure GetIsEntitled(): Boolean
    begin
        exit(NavApp.IsEntitled());
    end;
}
