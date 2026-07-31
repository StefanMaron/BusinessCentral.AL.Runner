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
}
