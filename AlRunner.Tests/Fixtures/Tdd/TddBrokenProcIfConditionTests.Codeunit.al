/// <summary>
/// References HasDiscount, a procedure "Tdd Target Cu" does not declare yet, used as an
/// `if <call> then` CONDITION — the acceptance table's `if Cust.HasLoyalty() then` anchor.
/// With --tdd this must generate HasDiscount(Arg1: Integer): Boolean with an empty body, which
/// returns false, so the test passes and its result names HasDiscount (#5147).
/// </summary>
codeunit 65014 "Tdd Broken Proc If Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure MissingBooleanProcedure_RunsAgainstGeneratedStub()
    var
        Target: Codeunit "Tdd Target Cu";
    begin
        if Target.HasDiscount(5) then
            Error('unreachable — HasDiscount is not yet implemented');
    end;
}
