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
        Assert.IsTrue(true, 'SetTableView + Run should compile and not crash');
    end;

    [Test]
    procedure RunRequestPageReturnsNonEmpty()
    var
        Runner: Codeunit "RH Report Runner";
        Result: Text;
    begin
        // [GIVEN] A report variable
        // [WHEN] RunRequestPage is called
        Result := Runner.RunRequestPageAndGetResult();

        // [THEN] A non-empty XML stub is returned
        Assert.AreNotEqual('', Result, 'RunRequestPage should return non-empty result');
    end;

    [Test]
    procedure ReportVariableCompiles()
    var
        Rep: Report "RH Test Report";
    begin
        // [GIVEN] A report variable declared
        // [WHEN] It is used in code
        Rep.SetTableView(Rec);

        // [THEN] Compilation succeeds
        Assert.IsTrue(true, 'Report variable should compile in standalone mode');
    end;

    var
        Rec: Record "RH Test Data";
}
