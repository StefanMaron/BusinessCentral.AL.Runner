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

    procedure GetIsEntitled(EntitlementId: Text): Boolean
    begin
        exit(NavApp.IsEntitled(EntitlementId));
    end;

    /// <summary>
    /// Calls NavApp.IsEntitled with a hardcoded string literal — this triggered CS1503
    /// (cannot convert from 'string' to 'NavText') before issue #1231 was fixed.
    /// </summary>
    procedure GetIsEntitledLiteral(): Boolean
    begin
        exit(NavApp.IsEntitled('standard_1'));
    end;
}
