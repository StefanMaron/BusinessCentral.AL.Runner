/// <summary>
/// References CalcTotal, a procedure "Tdd Target Cu" does not declare yet. Without
/// --tdd this whole object is excluded from the emit (BC's method-body check is not
/// gated on ContinueBuildOnError). With --tdd CalcTotal is generated with an empty body, the
/// test runs (it asserts nothing, so it passes) and its result names CalcTotal (#5147).
/// </summary>
codeunit 65010 "Tdd Broken Proc Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure MissingProcedure_RunsAgainstGeneratedStub()
    var
        Target: Codeunit "Tdd Target Cu";
        Result: Integer;
    begin
        Result := Target.CalcTotal(5);
    end;
}
