/// Minimal table needed so tests can pass a record Variant to Report.RunModal.
table 163000 "RRM4 Dummy"
{
    fields
    {
        field(1; Id; Integer) { }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
    }
}

/// Source codeunit that exercises Report.RunModal with 2-, 3-, and 4-argument forms.
codeunit 163001 "RRM4 Src"
{
    procedure CallRunModal1Arg(reportId: Integer)
    begin
        Report.RunModal(reportId);
    end;

    procedure CallRunModal2Arg(reportId: Integer; requestWindow: Boolean)
    begin
        Report.RunModal(reportId, requestWindow);
    end;

    procedure CallRunModal3Arg(reportId: Integer; requestWindow: Boolean; systemPrinter: Boolean)
    begin
        Report.RunModal(reportId, requestWindow, systemPrinter);
    end;

    procedure CallRunModal4Arg(reportId: Integer; requestWindow: Boolean; systemPrinter: Boolean; var rec: Record "RRM4 Dummy")
    begin
        Report.RunModal(reportId, requestWindow, systemPrinter, rec);
    end;
}
