codeunit 70502 "Report Skip Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure TestReportWithSkipRuns()
    var
        Helper: Codeunit "Report Skip Helper";
    begin
        // Running a report containing CurrReport.Skip() should not throw
        Helper.RunReportSkip();
        Assert.IsTrue(true, 'Report with CurrReport.Skip() should run without error');
    end;

    [Test]
    procedure TestReportSkipNoErrorOnSecondRun()
    var
        Helper: Codeunit "Report Skip Helper";
    begin
        // Running the same report twice should not accumulate state or error
        Helper.RunReportSkip();
        Helper.RunReportSkip();
        Assert.IsTrue(true, 'Report with Skip should be idempotent');
    end;
}
