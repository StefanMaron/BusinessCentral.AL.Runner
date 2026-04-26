codeunit 98002 "RP Report Preview Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure ReportPreview_NoThrow()
    var
        Runner: Codeunit "RP Preview Runner";
    begin
        // [GIVEN] A report that calls CurrReport.Preview() in OnInitReport
        // [WHEN]  The report is run via Rep.Run()
        // [THEN]  No exception is thrown — Preview() is a no-op stub in standalone mode
        Runner.RunPreviewReport();
        Assert.IsTrue(true, 'CurrReport.Preview() must not throw in standalone mode');
    end;
}
