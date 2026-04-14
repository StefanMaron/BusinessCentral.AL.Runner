codeunit 91002 "RH Report Handle Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure ReportSetTableViewAndRunDoNotCrash()
    var
        Runner: Codeunit "RH Report Runner";
        Rec: Record "RH Test Data";
    begin
        // [GIVEN] A record with data
        Rec.Id := 1;
        Rec.Name := 'Test';
        Rec.Insert(false);

        // [WHEN] Report.SetTableView and Run are called
        Runner.RunReportWithTableView(Rec);

        // [THEN] No crash — standalone mode stubs report execution
        // Verify the record still exists and wasn't corrupted by the report run
        Rec.FindFirst();
        Assert.AreEqual('Test', Rec.Name, 'Record should be intact after report Run()');
    end;

    [Test]
    procedure RunRequestPageWithoutHandlerThrows()
    var
        Runner: Codeunit "RH Report Runner";
    begin
        // [GIVEN] A report variable and no registered handler
        // [WHEN] RunRequestPage is called without a handler
        // [THEN] It should throw a descriptive error
        asserterror Runner.RunRequestPageAndGetResult();
        Assert.ExpectedError('No RequestPageHandler registered');
    end;

    [Test]
    procedure ReportVariableCompiles()
    var
        Rep: Report "RH Test Report";
        Rec: Record "RH Test Data";
    begin
        // [GIVEN] A report variable and a record
        Rec.Id := 10;
        Rec.Name := 'ReportTest';
        Rec.Insert(false);

        // [WHEN] SetTableView is called on the report variable
        Rep.SetTableView(Rec);

        // [THEN] The record data was not corrupted by SetTableView
        Rec.FindFirst();
        Assert.AreEqual(10, Rec.Id, 'Record Id should be intact after SetTableView');
        Assert.AreEqual('ReportTest', Rec.Name, 'Record Name should be intact after SetTableView');
    end;

    [Test]
    procedure ReportRunWithoutSetTableViewDoesNotCrash()
    var
        Runner: Codeunit "RH Report Runner";
    begin
        // [NEGATIVE] Running a report without SetTableView should not crash
        // (the report instance should handle having no table view set)
        Runner.RunReportWithoutTableView();
        Assert.IsTrue(true, 'Run without SetTableView should not crash');
    end;

    var
        Rec: Record "RH Test Data";
}
