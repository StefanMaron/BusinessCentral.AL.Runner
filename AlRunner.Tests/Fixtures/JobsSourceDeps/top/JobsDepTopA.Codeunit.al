codeunit 65720 "Jobs Dep Top A"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure TopA_ReachesBaseThroughMid()
    var
        Mid: Codeunit "Jobs Dep Mid";
    begin
        if Mid.Value() <> 11 then
            Error('TopA reaches the base through mid');
    end;
}
