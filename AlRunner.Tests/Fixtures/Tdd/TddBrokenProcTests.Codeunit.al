/// <summary>
/// References CalcTotal, a procedure "Tdd Target Cu" does not declare yet. Without
/// --tdd this whole object is excluded from the emit (BC's method-body check is not
/// gated on ContinueBuildOnError) and its [Test] procedure vanishes from the run. With
/// --tdd it must report FAILED, naming CalcTotal.
/// </summary>
codeunit 65010 "Tdd Broken Proc Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure MissingProcedure_ReportsFailedNotVanished()
    var
        Target: Codeunit "Tdd Target Cu";
        Result: Integer;
    begin
        Result := Target.CalcTotal(5);
    end;
}
