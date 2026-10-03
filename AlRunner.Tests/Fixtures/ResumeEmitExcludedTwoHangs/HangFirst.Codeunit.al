codeunit 50960 "Resume Two Hangs First"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure HangsFirst()
    begin
        while true do;
    end;
}
