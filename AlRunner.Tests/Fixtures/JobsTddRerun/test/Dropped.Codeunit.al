/// <summary>
/// Dropped in every pass: the call is a shape --tdd refuses to generate for (the call is the argument of
/// exit()), so it never compiles however many members are generated.
/// </summary>
codeunit 51121 "Jobs Rerun Dropped"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure RerunDropped_A()
    begin
        if Calc() <> 0 then
            Error('not reached: the codeunit was dropped');
    end;

    [Test]
    procedure RerunDropped_B()
    begin
    end;

    local procedure Calc(): Integer
    var
        Points: Codeunit "Jobs Rerun Points";
    begin
        exit(Points.RefusedMissing(1));
    end;
}
