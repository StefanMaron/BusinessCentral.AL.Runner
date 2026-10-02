codeunit 50961 "Claim Probe 1 RXT"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure Probe1_T1()
    begin
        Sleep(1500);
        if 1 + 1 <> 2 then
            Error('arithmetic broke in probe 1');
    end;

    [Test]
    procedure Probe1_T2()
    begin
        Sleep(1500);
        if 1 + 1 <> 2 then
            Error('arithmetic broke in probe 1');
    end;

    [Test]
    procedure Probe1_T3()
    begin
        Sleep(1500);
        if 1 + 1 <> 2 then
            Error('arithmetic broke in probe 1');
    end;
}
