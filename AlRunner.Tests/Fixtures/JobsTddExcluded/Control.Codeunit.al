codeunit 51003 "Jobs Tdd Control"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure Control_A()
    begin
        if 1 + 1 <> 2 then
            Error('arithmetic broke');
    end;

    [Test]
    procedure Control_B()
    begin
        if 2 + 2 <> 4 then
            Error('arithmetic broke');
    end;
}
