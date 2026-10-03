codeunit 65750 "Jobs Dep Solo Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure Solo_RunsWithNoDependencies()
    var
        Total: Integer;
    begin
        Total := 2 + 3;
        if Total <> 5 then
            Error('arithmetic holds');
    end;

    [Test]
    procedure Solo_AlsoRuns()
    begin
        if 'a' + 'b' <> 'ab' then
            Error('text concatenates');
    end;
}
