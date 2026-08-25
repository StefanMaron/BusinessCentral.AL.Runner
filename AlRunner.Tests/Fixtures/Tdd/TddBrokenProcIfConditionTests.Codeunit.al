/// <summary>
/// References HasDiscount, a procedure "Tdd Target Cu" does not declare yet, used as an
/// `if <call> then` CONDITION — the acceptance table's `if Cust.HasLoyalty() then` anchor.
/// With --tdd this must generate HasDiscount(Arg1: Integer): Boolean and report FAILED once
/// the generated stub's Error() fires (evaluating the condition reaches the stub body before
/// any branch), naming HasDiscount.
/// </summary>
codeunit 65014 "Tdd Broken Proc If Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure MissingBooleanProcedure_ReportsFailedNotVanished()
    var
        Target: Codeunit "Tdd Target Cu";
    begin
        if Target.HasDiscount(5) then
            Error('unreachable — HasDiscount is not yet implemented');
    end;
}
