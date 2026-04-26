codeunit 91001 "RS Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "RS Src";

    // ------------------------------------------------------------------
    // No-op methods: Run, RunModal
    // ------------------------------------------------------------------

    [Test]
    procedure Report_Run_IsNoOp()
    begin
        // [GIVEN] A report id with no registered handler
        // [WHEN]  We call Report.Run(id) with a non-existent report
        // [THEN]  No error — static method is a no-op in standalone mode
        Src.CallRun(99999);
    end;

    [Test]
    procedure Report_RunModal_IsNoOp()
    begin
        Src.CallRunModal(99999);
    end;

    // ------------------------------------------------------------------
    // SaveAs* methods: no file I/O in standalone mode
    // ------------------------------------------------------------------

    [Test]
    procedure Report_SaveAsPdf_IsNoOp()
    begin
        Src.CallSaveAsPdf(99999, 'report.pdf');
    end;

    [Test]
    procedure Report_SaveAsWord_IsNoOp()
    begin
        Src.CallSaveAsWord(99999, 'report.docx');
    end;

    [Test]
    procedure Report_SaveAsExcel_IsNoOp()
    begin
        Src.CallSaveAsExcel(99999, 'report.xlsx');
    end;

    [Test]
    procedure Report_SaveAsXml_IsNoOp()
    begin
        Src.CallSaveAsXml(99999, 'report.xml');
    end;

    // ------------------------------------------------------------------
    // Value-returning methods
    // ------------------------------------------------------------------

    [Test]
    procedure Report_GetSubstituteReportId_ReturnsSameId()
    begin
        // [GIVEN] A report id
        // [WHEN]  Report.GetSubstituteReportId(id) is called
        // [THEN]  Returns the same id (no substitution in standalone mode)
        Assert.AreEqual(50100, Src.GetSubstituteId(50100), 'GetSubstituteReportId should return input id');
    end;

    [Test]
    procedure Report_RunRequestPage_ReturnsEmpty()
    begin
        // [GIVEN] A report id with no registered RequestPageHandler
        // [WHEN]  Report.RunRequestPage(id) is called
        // [THEN]  Returns empty string (no UI in standalone mode)
        Assert.AreEqual('', Src.GetRunRequestPage(99999), 'RunRequestPage should return empty string');
    end;
}
