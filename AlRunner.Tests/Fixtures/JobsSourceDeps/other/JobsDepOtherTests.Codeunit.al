codeunit 65740 "Jobs Dep Other Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure Other_SeesMid()
    var
        Mid: Codeunit "Jobs Dep Mid";
    begin
        if Mid.Value() <> 11 then
            Error('Mid returns the base value plus one');
    end;

    [Test]
    procedure Other_SeesMidTwice()
    var
        Mid: Codeunit "Jobs Dep Mid";
    begin
        if Mid.Value() + Mid.Value() <> 22 then
            Error('Mid is stable');
    end;
}
