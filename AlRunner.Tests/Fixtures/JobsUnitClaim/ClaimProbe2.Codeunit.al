codeunit 50962 "Claim Probe 2 RXT"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure Probe2_T1()
    begin
        Sleep(1500);
        if 1 + 2 <> 3 then
            Error('arithmetic broke in probe 2');
    end;
}
