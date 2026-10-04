codeunit 65770 "Jobs Outputs B Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure B_Passes()
    begin
        if 2 + 3 <> 5 then
            Error('arithmetic holds');
    end;

    [Test]
    procedure B_FailsOnPurpose()
    begin
        Error('b fails on purpose');
    end;
}
