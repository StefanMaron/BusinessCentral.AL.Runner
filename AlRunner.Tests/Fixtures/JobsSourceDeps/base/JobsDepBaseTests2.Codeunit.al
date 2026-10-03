codeunit 65702 "Jobs Dep Base Tests 2"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure BaseValue_IsNot11()
    var
        Base: Codeunit "Jobs Dep Base";
    begin
        if Base.Value() = 11 then
            Error('Base does not return 11');
    end;
}
