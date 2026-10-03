codeunit 50961 "Resume Two Hangs Second"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure HangsSecond()
    begin
        while true do;
    end;
}
