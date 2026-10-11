codeunit 65402 "Package Closed Enum Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure NonExtensibleEnum_IsRefused()
    var
        T: Enum "Closed Tier";
    begin
        T := "Closed Tier"::Omega;
    end;
}
