codeunit 56591 "DF Probe Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure EvaluateThenValidateDateFormula()
    var
        R: Record "DF Probe Row";
    begin
        // [GIVEN] Evaluate a text literal into a record's DateFormula field
        R.Id := 1;
        Evaluate(R.Formula, '<1D>');
        // [WHEN] Single-arg Validate on that field
        R.Validate(Formula);
        // [THEN] Execution reaches the insert + assertion without casting error
        R.Insert();
        Assert.AreEqual(1, R.Id, 'Id should be 1 after Evaluate + Validate + Insert');
    end;

    [Test]
    procedure EvaluateViaLocalThenAssignAndValidate()
    var
        R: Record "DF Probe Row";
        Parsed: DateFormula;
    begin
        R.Id := 2;
        Evaluate(Parsed, '<1W>');
        R.Formula := Parsed;
        R.Validate(Formula);
        R.Insert();
        Assert.AreEqual(2, R.Id, 'Id should be 2 after local Evaluate path');
    end;
}
