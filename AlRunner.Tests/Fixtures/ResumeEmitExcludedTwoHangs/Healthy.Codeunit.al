codeunit 50962 "Resume Two Hangs Healthy"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure Healthy_A()
    begin
        if 1 + 1 <> 2 then
            Error('arithmetic broke');
    end;

    [Test]
    procedure Healthy_B()
    begin
        if 2 + 2 <> 4 then
            Error('arithmetic broke');
    end;
}
