codeunit 65722 "Jobs Dep Top C"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure TopC_ReachesBaseThroughMid()
    var
        Mid: Codeunit "Jobs Dep Mid";
    begin
        if Mid.Value() = 10 then
            Error('TopC reaches the base through mid');
    end;
}
