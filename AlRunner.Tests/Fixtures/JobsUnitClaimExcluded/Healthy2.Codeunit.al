codeunit 50982 "Jobs Excl Healthy 2"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure Healthy2_A()
    begin
        if 1 + 1 <> 2 then
            Error('arithmetic broke');
    end;

    [Test]
    procedure Healthy2_B()
    begin
        if 2 + 2 <> 4 then
            Error('arithmetic broke');
    end;
}
