/// Source codeunit exercising Report.SaveAs/SaveAsPdf/SaveAsWord/SaveAsExcel/SaveAsHtml/SaveAsXml
/// in a Boolean context (i.e. `if Report.SaveAs(...) then`).
/// This verifies that the static stub overloads return bool, not void.
table 1319000 "RSB Blob"
{
    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; Content; Blob) { }
    }
    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}

codeunit 1319001 "Report SaveAs Bool Src"
{
    /// Returns true when Report.SaveAs used as Boolean returns true (no-op stub).
    procedure SaveAsReturnsTrue(ReportId: Integer; RequestData: Text): Boolean
    var
        Rec: Record "RSB Blob";
        OS: OutStream;
        Format: ReportFormat;
    begin
        Rec.Content.CreateOutStream(OS);
        Format := ReportFormat::Pdf;
        if Report.SaveAs(ReportId, RequestData, Format, OS) then
            exit(true);
        exit(false);
    end;

    /// Returns true when Report.SaveAs (5-arg with RecordRef) used as Boolean returns true.
    procedure SaveAsRecordRefReturnsTrue(ReportId: Integer; RequestData: Text): Boolean
    var
        Rec: Record "RSB Blob";
        RecRef: RecordRef;
        OS: OutStream;
        Format: ReportFormat;
    begin
        Rec.Content.CreateOutStream(OS);
        Format := ReportFormat::Pdf;
        if Report.SaveAs(ReportId, RequestData, Format, OS, RecRef) then
            exit(true);
        exit(false);
    end;

    /// Returns true when Report.SaveAsPdf used as Boolean returns true.
    procedure SaveAsPdfReturnsTrue(ReportId: Integer; FileName: Text): Boolean
    begin
        if Report.SaveAsPdf(ReportId, FileName) then
            exit(true);
        exit(false);
    end;

    /// Returns true when Report.SaveAsWord used as Boolean returns true.
    procedure SaveAsWordReturnsTrue(ReportId: Integer; FileName: Text): Boolean
    begin
        if Report.SaveAsWord(ReportId, FileName) then
            exit(true);
        exit(false);
    end;

    /// Returns true when Report.SaveAsExcel used as Boolean returns true.
    procedure SaveAsExcelReturnsTrue(ReportId: Integer; FileName: Text): Boolean
    begin
        if Report.SaveAsExcel(ReportId, FileName) then
            exit(true);
        exit(false);
    end;

    /// Returns true when Report.SaveAsHtml used as Boolean returns true.
    procedure SaveAsHtmlReturnsTrue(ReportId: Integer; FileName: Text): Boolean
    begin
        if Report.SaveAsHtml(ReportId, FileName) then
            exit(true);
        exit(false);
    end;

    /// Returns true when Report.SaveAsXml used as Boolean returns true.
    procedure SaveAsXmlReturnsTrue(ReportId: Integer; FileName: Text): Boolean
    begin
        if Report.SaveAsXml(ReportId, FileName) then
            exit(true);
        exit(false);
    end;
}
