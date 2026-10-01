/// <summary>
/// References CalcSubtotal, a procedure "Tdd Target Cu" does not declare yet, called as a
/// NESTED argument to an already-resolvable procedure (Assert.AreEqual) — the return-type
/// anchor from the acceptance table's own example (`Assert.AreEqual(100, Cu.CalcTotal())`),
/// distinct from TddBrokenProcTests' assignment-target anchor. With --tdd this must generate
/// CalcSubtotal(Arg1: Integer): Integer with an empty body, and the test fails on its OWN
/// assertion (Expected 100, Actual 0), its result naming CalcSubtotal (#5147).
/// </summary>
codeunit 65013 "Tdd Broken Proc Nested Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "Tdd Assert";

    [Test]
    procedure MissingProcedureNestedArg_FailsOnItsOwnAssertion()
    var
        Target: Codeunit "Tdd Target Cu";
    begin
        Assert.AreEqual(100, Target.CalcSubtotal(5), 'CalcSubtotal(5) should be 100');
    end;
}
