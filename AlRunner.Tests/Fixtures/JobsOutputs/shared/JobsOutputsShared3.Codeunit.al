codeunit 65783 "Jobs Outputs Shared 3 Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure Shared3_Passes()
    begin
        Sleep(1500);
        if 2 + 3 <> 5 then
            Error('arithmetic holds');
    end;
}
