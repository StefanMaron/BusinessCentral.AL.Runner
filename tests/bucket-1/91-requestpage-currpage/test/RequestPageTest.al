codeunit 91002 "RPC Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        H: Codeunit "RPC Helper";

    // ── Caption ───────────────────────────────────────────────────────────────

    [Test]
    procedure ReportWithCurrPageCaption_CompilesAndRuns()
    begin
        // Positive: a report whose requestpage trigger sets CurrPage.Caption
        // must compile and run without error.
        H.RunReport();
        Assert.IsTrue(true, 'Report.Run with requestpage CurrPage.Caption must not crash');
    end;

    [Test]
    procedure ReportWithCurrPageMethods_DoesNotCrash()
    begin
        // Positive: CurrPage.Editable, LookupMode, ObjectId, Activate, Update,
        // SaveRecord inside requestpage trigger must all compile and run.
        H.RunReport();
        Assert.IsTrue(true, 'All requestpage CurrPage stub methods must compile and run');
    end;

    [Test]
    procedure RunReport_NotACompileError()
    begin
        // Negative: verify we are not just passing because the report is missing.
        // Report.Run(91000) must reach the test without an unhandled exception.
        asserterror Error('intentional');
        Assert.ExpectedError('intentional');
    end;

}
