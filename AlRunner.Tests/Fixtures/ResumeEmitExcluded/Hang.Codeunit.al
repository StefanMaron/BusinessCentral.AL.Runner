codeunit 50980 "Resume Excl Hang First"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure RanBeforeHang()
    begin
    end;

    [Test]
    procedure Hangs()
    begin
        while true do;
    end;

    [Test]
    procedure AbandonedInFirst()
    begin
    end;
}
