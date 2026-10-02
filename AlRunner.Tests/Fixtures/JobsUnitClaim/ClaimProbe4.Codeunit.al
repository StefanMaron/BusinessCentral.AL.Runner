codeunit 50964 "Claim Probe 4 RXT"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure Probe4_T1()
    begin
        Sleep(1500);
        if 1 + 4 <> 5 then
            Error('arithmetic broke in probe 4');
    end;
}
