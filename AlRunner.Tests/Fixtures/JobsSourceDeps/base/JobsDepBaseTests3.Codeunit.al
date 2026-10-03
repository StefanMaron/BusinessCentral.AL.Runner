codeunit 65703 "Jobs Dep Base Tests 3"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure BaseValue_IsPositive()
    var
        Base: Codeunit "Jobs Dep Base";
    begin
        if Base.Value() <= 0 then
            Error('Base returns a positive value');
    end;
}
