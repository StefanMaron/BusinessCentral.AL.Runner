codeunit 50967 "Claim Probe 7 RXT"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure Probe7_T1()
    begin
        Sleep(1500);
        if 1 + 7 <> 8 then
            Error('arithmetic broke in probe 7');
    end;
}
