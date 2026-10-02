codeunit 50963 "Claim Probe 3 RXT"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure Probe3_T1()
    begin
        Sleep(1500);
        if 1 + 3 <> 4 then
            Error('arithmetic broke in probe 3');
    end;
}
