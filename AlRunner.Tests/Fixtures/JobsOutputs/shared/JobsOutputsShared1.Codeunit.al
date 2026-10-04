codeunit 65781 "Jobs Outputs Shared 1 Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure Shared1_Passes()
    begin
        Sleep(1500);
        if 2 + 3 <> 5 then
            Error('arithmetic holds');
    end;
}
