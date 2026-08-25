/// <summary>
/// References DoubleIt, a procedure "Tdd Watch Target Cu" does not declare yet.
/// Without --tdd this whole object is excluded from the emit. With --tdd it must
/// report FAILED, naming DoubleIt — until "Tdd Watch Target Cu" implements it, at
/// which point it must report PASSED with the doubled value.
/// </summary>
codeunit 65101 "Tdd Watch Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure MissingProcedure_ReportsFailedThenPasses()
    var
        Target: Codeunit "Tdd Watch Target Cu";
        Result: Integer;
    begin
        Result := Target.DoubleIt(5);
        if Result <> 10 then
            Error('expected DoubleIt(5) = 10, got %1', Result);
    end;
}
