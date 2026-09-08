// Fixture suite for PartialCompanyInitializationTests.DoesNotLowerAnEarnedExitCode (#3538).
//
// Deliberately RED. Its sibling fixture CompanyInitPartial is deliberately green, and the pair
// is what separates the two exit codes: a clean run plus an abort must report 2, and a run that
// has earned 1 by failing a test must keep 1 while still recording the abort.
codeunit 70700 "CIP Failing Fixture Tests"
{
    Subtype = Test;

    [Test]
    procedure ArithmeticIsWrongOnPurpose()
    var
        Sum: Integer;
    begin
        Sum := 2 + 40;
        if Sum <> 41 then
            Error('deliberate failure: expected 41, got %1', Sum);
    end;
}
