codeunit 50968 "Claim Probe 8 RXT"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure Probe8_T1()
    begin
        Sleep(1500);
        if 1 + 8 <> 9 then
            Error('arithmetic broke in probe 8');
    end;

    [Test]
    procedure Probe8_T2()
    begin
        Sleep(1500);
        if 1 + 8 <> 9 then
            Error('arithmetic broke in probe 8');
    end;

    [Test]
    procedure Probe8_T3()
    begin
        Sleep(1500);
        if 1 + 8 <> 9 then
            Error('arithmetic broke in probe 8');
    end;
}
