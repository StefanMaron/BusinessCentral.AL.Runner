/// Tests proving ReportInstance.ShowOutput(Boolean) and
/// ReportInstance.CreateTotals(Decimal, Decimal) are no-op stubs (issue #1379).
codeunit 310101 "RSO Test"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    [Test]
    procedure ShowOutput_False_NoThrow()
    var
        Src: Codeunit "RSO Source";
    begin
        // [GIVEN] A report whose OnAfterGetRecord trigger calls CurrReport.ShowOutput(false)
        // [WHEN]  We run the report
        // [THEN]  No error is raised — ShowOutput is a no-op stub in standalone mode
        Src.RunReport_NoThrow();
        Assert.IsTrue(true, 'ShowOutput(false) must not throw in standalone mode');
    end;

    [Test]
    procedure CreateTotals_TwoDecimals_NoThrow()
    var
        Src: Codeunit "RSO Source";
    begin
        // [GIVEN] A report whose OnAfterGetRecord trigger calls CurrReport.CreateTotals(Amount, Amount)
        // [WHEN]  We run the report
        // [THEN]  No error is raised — CreateTotals(Decimal,Decimal) is a no-op stub in standalone mode
        Src.RunReport_NoThrow();
        Assert.IsTrue(true, 'CreateTotals(Decimal,Decimal) must not throw in standalone mode');
    end;
}
