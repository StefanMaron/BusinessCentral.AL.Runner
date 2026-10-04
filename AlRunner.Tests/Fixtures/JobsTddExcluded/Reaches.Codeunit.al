codeunit 51002 "Jobs Tdd Reaches"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure ReachesDropped()
    begin
        Codeunit.Run(51001);
    end;
}
