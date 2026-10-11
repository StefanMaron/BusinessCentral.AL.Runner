codeunit 65404 "Package Sealed Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure NonExtensibleTable_IsRefused()
    var
        E: Record "Sealed Entry";
    begin
        E.Id := 1;
        E.Extra := 3;
    end;
}
