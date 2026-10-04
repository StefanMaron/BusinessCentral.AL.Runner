codeunit 65760 "Jobs Outputs A Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure A_Passes()
    begin
        if 2 + 3 <> 5 then
            Error('arithmetic holds');
    end;

    [Test]
    procedure A_FailsOnPurpose()
    begin
        Error('a fails on purpose');
    end;
}
