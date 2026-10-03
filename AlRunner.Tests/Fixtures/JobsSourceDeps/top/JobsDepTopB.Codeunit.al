codeunit 65721 "Jobs Dep Top B"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure TopB_ReachesBaseThroughMid()
    var
        Mid: Codeunit "Jobs Dep Mid";
    begin
        if Mid.Value() + 1 <> 12 then
            Error('TopB reaches the base through mid');
    end;
}
