/// Tests for Text-name Report overloads and RunRequestPage 2-arg variant — issue #1377.
codeunit 309701 "RptTextName Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ── Report.Execute(Text, Text, RecordRef) ─────────────────────────────

    [Test]
    procedure Report_Execute_TextName_IsNoOp()
    var
        Src: Codeunit "RptTextName Src";
        RecRef: RecordRef;
    begin
        // [GIVEN] A report name (non-existent), request XML, and an unbound RecordRef
        // [WHEN]  Report.Execute(reportName, requestPageXml, RecordRef) is called
        // [THEN]  No error — overload is a no-op in standalone mode
        Src.CallExecuteTextName('RptTextName Report', '<ReqPage />', RecRef);
    end;

    // ── Report.Run(Text, Boolean, Boolean, Table) ─────────────────────────

    [Test]
    procedure Report_Run_TextName_IsNoOp()
    var
        Src: Codeunit "RptTextName Src";
        Rec: Record "RptTextName Table";
    begin
        // [GIVEN] A report name, request-page/printer flags, and an empty record
        // [WHEN]  Report.Run(reportName, false, false, Rec) is called
        // [THEN]  No error — overload is a no-op when report type is unknown
        Src.CallRunTextName('RptTextName Report', false, false, Rec);
    end;

    [Test]
    procedure Report_Run_TextName_WithData_IsNoOp()
    var
        Src: Codeunit "RptTextName Src";
        Rec: Record "RptTextName Table";
    begin
        // [GIVEN] A record with data inserted
        Rec.Init();
        Rec."No." := 'TN-001';
        Rec.Amount := 100;
        Rec.Insert();

        // [WHEN]  Report.Run(reportName, false, false, Rec) is called
        // [THEN]  No error — overload completes without exception
        Src.CallRunTextName('RptTextName Report', false, false, Rec);
    end;

    // ── Report.RunModal(Text, Boolean, Boolean, Table) ────────────────────

    [Test]
    procedure Report_RunModal_TextName_IsNoOp()
    var
        Src: Codeunit "RptTextName Src";
        Rec: Record "RptTextName Table";
    begin
        // [GIVEN] A report name, request-page/printer flags, and an empty record
        // [WHEN]  Report.RunModal(reportName, false, false, Rec) is called
        // [THEN]  No error — overload is a no-op when report type is unknown
        Src.CallRunModalTextName('RptTextName Report', false, false, Rec);
    end;

    // ── Report.RunRequestPage(Integer, Text) ─────────────────────────────

    [Test]
    procedure Report_RunRequestPage_IntText_ReturnsEmpty()
    var
        Src: Codeunit "RptTextName Src";
        Result: Text;
    begin
        // [GIVEN] A report id and non-empty request parameters
        // [WHEN]  Report.RunRequestPage(reportId, requestParameters) is called
        // [THEN]  Returns empty string in standalone mode (no request-page UI)
        Result := Src.CallRunRequestPage2Arg(99999, '<ReqParams />');
        Assert.AreEqual('', Result, 'RunRequestPage(Integer,Text) must return empty string in standalone mode');
    end;

    [Test]
    procedure Report_RunRequestPage_IntText_EmptyParams_ReturnsEmpty()
    var
        Src: Codeunit "RptTextName Src";
        Result: Text;
    begin
        // [GIVEN] A report id and an empty request-parameters string
        // [WHEN]  Report.RunRequestPage(reportId, '') is called
        // [THEN]  Returns empty string in standalone mode
        Result := Src.CallRunRequestPage2Arg(99999, '');
        Assert.AreEqual('', Result, 'RunRequestPage(Integer,Text) with empty params must return empty string in standalone mode');
    end;
}
