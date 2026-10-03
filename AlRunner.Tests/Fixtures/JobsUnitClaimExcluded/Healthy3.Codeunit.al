codeunit 50983 "Jobs Excl Healthy 3"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure Healthy3_A()
    begin
        if 1 + 1 <> 2 then
            Error('arithmetic broke');
    end;

    [Test]
    procedure Healthy3_B()
    begin
        if 2 + 2 <> 4 then
            Error('arithmetic broke');
    end;
}
