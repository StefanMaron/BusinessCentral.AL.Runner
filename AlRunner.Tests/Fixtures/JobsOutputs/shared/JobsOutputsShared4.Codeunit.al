codeunit 65784 "Jobs Outputs Shared 4 Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure Shared4_FailsOnPurpose()
    begin
        Sleep(1500);
        Error('shared 4 fails on purpose');
    end;
}
