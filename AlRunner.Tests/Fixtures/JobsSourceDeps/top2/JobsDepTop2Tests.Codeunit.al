codeunit 65730 "Jobs Dep Top Two Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure BothFolders_AgreeOnTheBase()
    var
        Base: Codeunit "Jobs Dep Base";
        Mid: Codeunit "Jobs Dep Mid";
    begin
        if Mid.Value() <> Base.Value() + 1 then
            Error('mid is the base plus one');
    end;

    [Test]
    procedure Base_IsReachedDirectly()
    var
        Base: Codeunit "Jobs Dep Base";
    begin
        if Base.Value() <> 10 then
            Error('Base returns 10');
    end;
}
