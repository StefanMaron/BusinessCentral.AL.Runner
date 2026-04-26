/// Helper table providing a Blob field for OutStream creation in tests.
table 98200 "RSRR Blob"
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

/// Exercises Report.SaveAs(ReportId; RequestData; Format; var OutStream; RecordRef) —
/// the 5-arg static overload with a RecordRef filter parameter.
codeunit 98201 "Report SaveAs RecordRef Src"
{
    procedure SaveAsWithRecordRef(ReportId: Integer; RequestData: Text)
    var
        Rec: Record "RSRR Blob";
        RecRef: RecordRef;
        OS: OutStream;
        Format: ReportFormat;
    begin
        Rec.Content.CreateOutStream(OS);
        Format := ReportFormat::Pdf;
        Report.SaveAs(ReportId, RequestData, Format, OS, RecRef);
    end;

    procedure SaveAsWithoutRecordRef(ReportId: Integer; RequestData: Text)
    var
        Rec: Record "RSRR Blob";
        OS: OutStream;
        Format: ReportFormat;
    begin
        Rec.Content.CreateOutStream(OS);
        Format := ReportFormat::Pdf;
        Report.SaveAs(ReportId, RequestData, Format, OS);
    end;
}
