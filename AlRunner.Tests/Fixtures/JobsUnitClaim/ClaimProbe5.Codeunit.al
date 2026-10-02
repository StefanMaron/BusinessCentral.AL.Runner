codeunit 50965 "Claim Probe 5 RXT"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure Probe5_T1()
    begin
        Sleep(1500);
        if 1 + 5 <> 6 then
            Error('arithmetic broke in probe 5');
    end;
}
