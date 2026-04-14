codeunit 70502 "Report Skip Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure TestReportWithSkipCompiles()
    var
        Helper: Codeunit "Report Skip Helper";
    begin
        // Positive: report containing CurrReport.Skip() compiles and runs
        Assert.IsTrue(Helper.RunReportSkip(), 'Report with Skip should compile');
    end;

    [Test]
    procedure TestReportSkipNegative()
    var
        Helper: Codeunit "Report Skip Helper";
    begin
        // Negative: verify the helper actually returns a value
        Assert.AreNotEqual(false, Helper.RunReportSkip(), 'Should return true not false');
    end;
}
