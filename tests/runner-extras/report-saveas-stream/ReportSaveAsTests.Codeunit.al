// M1 of the report-execution build — the dataset spine.
//
// RED (before): the runner Cecil-rewrote NavReport.SaveAsAsync to a blanket
// out-of-scope throw, so Report.SaveAs(...) of ANY format failed with
// "out-of-scope: NavReport.SaveAs — report-rendering" without executing the
// report at all.
//
// GREEN (after): the real BC chain runs in-process —
//   SaveAsAsync → SaveReportAsFormatCoreAsync → RunReportInternalCoreAsync →
//   ExecuteDataItemIteratorAsync (real data-item iteration over the in-memory
//   table provider) → ReportProcessorXmlGenerator → decorator chain → the
//   caller's OutStream.
// The out-of-scope boundary moves to the ReportResultSetProcessorFactory fork:
// only genuinely external processors (RDLC/Word/Excel render, print server,
// document service) throw, with reason report-rendering-external.
codeunit 60704 "RSS Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "RSS Assert";

    // Positive: the dataset XML must contain the exact value the test inserted —
    // proving real data-item iteration, not a canned/empty dataset.
    [Test]
    procedure SaveAsXml_StreamContainsInsertedValue()
    var
        Sample: Record "RSS Sample";
        BlobRec: Record "RSS Sample";
        OutStr: OutStream;
        InStr: InStream;
        Line: Text;
        Content: Text;
    begin
        Sample.DeleteAll();
        Sample."Entry No." := 1;
        Sample.Description := 'RSSMARKER-1f2a3b4c';
        Sample.Amount := 42.5;
        Sample.Insert();

        BlobRec."Blob Data".CreateOutStream(OutStr);
        Assert.IsTrue(
            Report.SaveAs(Report::"RSS Fixture Report", '', ReportFormat::Xml, OutStr),
            'Report.SaveAs(Xml) must return true');

        BlobRec."Blob Data".CreateInStream(InStr);
        while not InStr.EOS() do begin
            InStr.ReadText(Line);
            Content += Line;
        end;

        Assert.Contains(Content, '<', 'SaveAs(Xml) output must be XML');
        Assert.Contains(Content, 'RSSMARKER-1f2a3b4c',
            'dataset XML must contain the inserted Description value');
    end;

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
