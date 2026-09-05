// The dataset-spine positive proof (Report.SaveAs(Xml) actually running real data-item
// iteration over the in-memory table provider) is real BC semantics and migrated upstream
// to the al-language corpus (tests/al-language, handlers/TestReportSaveAsStream.al). Only
// the negative stays here: it asserts a runner-specific OutOfScope classification (RDLC
// rendering is genuinely external — the runner has no service tier to render with), not
// real BC behavior.
codeunit 60704 "RSS Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "RSS Assert";

    // Negative: rendering through the RDLC processor is genuinely external —
    // the factory fork must throw loudly with the documented reason.
    [Test]
    procedure SaveAsPdf_RdlcLayout_ThrowsExternalRenderingOos()
    var
        BlobRec: Record "RSS Sample";
        OutStr: OutStream;
    begin
        BlobRec."Blob Data".CreateOutStream(OutStr);
        asserterror Report.SaveAs(Report::"RSS Fixture Report", '', ReportFormat::Pdf, OutStr);
        Assert.Contains(GetLastErrorText(), 'report-rendering-external',
            'PDF render of an RDLC layout must throw the factory-fork OOS reason');
    end;

    // #2887 negative: a [RequestPageHandler] that asks for a RENDERED artifact must reach the
    // rendering refusal.
    //
    // What this catches. Every ALSaveAs* on a TestRequestPage parks a file name on the
    // session (NavTestPage.ALSaveAsExcel sets ReportOutputFileName, ReportOutputFormat =
    // FormResult.Excel, and invokes the built-in Excel action). The runner's stand-in for
    // BC's ReportResultSetProcessorFactory.GetTestResultProcessor used to install the XML
    // dataset renderer whenever a file name was parked, without looking at the format BC
    // branches on first — so SaveAsExcel got an XML dataset written into the .xlsx path it
    // named, the run reported success, and the rendering step was skipped because a dataset
    // had been "written". Six Tests-SINGLESERVER tests in Codeunit134335 then failed inside
    // the toolkit's OpenXml reader with "File contains corrupted data", naming a corrupt
    // workbook instead of the unsupported surface.
    //
    // The handler names a file this test never creates and never reads: with the fix nothing
    // writes it, and asserting on the ERROR rather than on the filesystem is what makes the
    // claim "it refused" rather than "it wrote something else".
    [Test]
    [HandlerFunctions('SaveAsExcelRequestPageHandler')]
    procedure RequestPageSaveAsExcel_RefusesRenderingLoudly()
    begin
        asserterror Report.Run(Report::"RSS Fixture Report", true, false);

        Assert.Contains(GetLastErrorText(), 'out-of-scope:',
            'A [RequestPageHandler] asking for Excel must reach the documented out-of-scope refusal, not be answered with a dataset');
        Assert.Contains(GetLastErrorText(), 'rendering',
            'the refusal must name RENDERING as the unsupported surface — the whole point is that the caller learns what is missing at the call, not several statements later in a workbook reader');
    end;

    [RequestPageHandler]
    procedure SaveAsExcelRequestPageHandler(var FixtureRequestPage: TestRequestPage "RSS Fixture Report")
    begin
        FixtureRequestPage.SaveAsExcel('rss-excel-must-never-be-written.xlsx');
    end;
}
