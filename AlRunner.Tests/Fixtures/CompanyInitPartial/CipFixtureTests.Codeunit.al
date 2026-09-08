// Fixture suite for PartialCompanyInitializationTests.cs (#3538).
//
// Deliberately trivial and deliberately GREEN. The condition under test is not something AL
// can observe — the runner's own record of a company initialization that did not complete —
// and the assertions in the C# test are about what happens to THIS result: it must still be
// reported as a pass, in the totals and in every output document, while the run as a whole
// stops being clean. A suite that failed here could not distinguish "the run is not clean
// because the company is partial" from "the run is not clean because a test failed".
codeunit 70680 "CIP Fixture Tests"
{
    Subtype = Test;

    [Test]
    procedure ArithmeticStillRuns()
    var
        Sum: Integer;
    begin
        Sum := 2 + 40;
        if Sum <> 42 then
            Error('expected 42, got %1', Sum);
    end;
}
