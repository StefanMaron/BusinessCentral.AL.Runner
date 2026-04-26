/// Source objects for Report.Run 2-arg overload test (issue #1427).
/// BC emits Report.Run(ReportId, RequestPage) — two arguments, no SystemPrinter.
report 310200 "RR2 Report"
{
    dataset
    {
        dataitem(DataLine; "RR2 Table")
        {
        }
    }
}

table 310200 "RR2 Table"
{
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Value; Integer) { }
    }
    keys { key(PK; "No.") { Clustered = true; } }
}

/// Exercises Report.Run with 2 arguments: (ReportId, RequestPage)
codeunit 310201 "RR2 Source"
{
    procedure CallRunTwoArgs(ReportId: Integer)
    begin
        Report.Run(ReportId, false);
    end;

    procedure CallRunTwoArgsRequestPageTrue(ReportId: Integer)
    begin
        Report.Run(ReportId, true);
    end;
}
