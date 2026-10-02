codeunit 50966 "Claim Probe 6 RXT"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure Probe6_T1()
    begin
        Sleep(1500);
        if 1 + 6 <> 7 then
            Error('arithmetic broke in probe 6');
    end;
}
