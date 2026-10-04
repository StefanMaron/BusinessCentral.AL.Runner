codeunit 65782 "Jobs Outputs Shared 2 Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure Shared2_FailsOnPurpose()
    begin
        Sleep(1500);
        Error('shared 2 fails on purpose');
    end;
}
