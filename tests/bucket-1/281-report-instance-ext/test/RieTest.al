codeunit 128002 "RIE Test"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    // ── Static Execute ────────────────────────────────────────────────────────

    [Test]
    procedure Execute_NoThrow()
    var
        Src: Codeunit "RIE Source";
    begin
        // Positive: Report.Execute(id, requestPage) must not throw in standalone mode.
        Src.Execute_NoOp();
        Assert.IsTrue(true, 'Report.Execute must not throw');
    end;

    // ── Instance Print ────────────────────────────────────────────────────────

    [Test]
    procedure Print_NoThrow()
    var
        Src: Codeunit "RIE Source";
    begin
        // Positive: Rep.Print(requestPage) must not throw in standalone mode.
        Src.Print_NoOp();
        Assert.IsTrue(true, 'Rep.Print must not throw');
    end;

    // ── Instance SaveAs ───────────────────────────────────────────────────────

    [Test]
    procedure SaveAs_NoThrow()
    var
        Src: Codeunit "RIE Source";
    begin
        // Positive: Rep.SaveAs(requestPage, format, outStream) must not throw.
        Src.SaveAs_NoOp();
        Assert.IsTrue(true, 'Rep.SaveAs must not throw');
    end;

    // ── NewPagePerRecord (instance property setter) ───────────────────────────

    [Test]
    procedure NewPagePerRecord_NoThrow()
    var
        Src: Codeunit "RIE Source";
    begin
        // Positive: Rep.NewPagePerRecord := true must compile and run without error.
        Src.NewPagePerRecord_Set();
        Assert.IsTrue(true, 'Rep.NewPagePerRecord setter must not throw');
    end;

    // ── ValidateAndPrepareLayout (static) ─────────────────────────────────────

    [Test]
    procedure ValidateAndPrepareLayout_NoThrow()
    var
        Src: Codeunit "RIE Source";
    begin
        // Positive: Report.ValidateAndPrepareLayout must not throw in standalone mode.
        Src.ValidateAndPrepareLayout_NoOp();
        Assert.IsTrue(true, 'Report.ValidateAndPrepareLayout must not throw');
    end;

    // ── CurrReport.* via report run (NewPage, PageNo, PaperSource, etc.) ──────

    [Test]
    procedure CurrReportMethods_NoThrow()
    var
        Src: Codeunit "RIE Source";
    begin
        // Positive: Running a report that uses CurrReport.NewPage/PageNo/PaperSource/
        // ShowOutput/TotalsCausedBy/NewPagePerRecord inside if-false must not throw.
        Src.RunWithCurrReportMethods_NoThrow();
        Assert.IsTrue(true, 'Report with CurrReport.* in if-false block must not throw');
    end;
}
