codeunit 50981 "Resume Excl Healthy 1"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure Healthy1_A()
    begin
        if 1 + 1 <> 2 then
            Error('arithmetic broke');
    end;

    [Test]
    procedure Healthy1_B()
    begin
        if 2 + 2 <> 4 then
            Error('arithmetic broke');
    end;
}
