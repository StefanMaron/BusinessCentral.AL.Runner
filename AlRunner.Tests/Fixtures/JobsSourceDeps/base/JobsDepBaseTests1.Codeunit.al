/// <summary>A dependency folder with tests of its own: they must be reported once, by the worker
/// that runs this folder, however many workers compile it as somebody's dependency.</summary>
codeunit 65701 "Jobs Dep Base Tests 1"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure BaseValue_Is10()
    var
        Base: Codeunit "Jobs Dep Base";
    begin
        if Base.Value() <> 10 then
            Error('Base returns 10');
    end;
}
