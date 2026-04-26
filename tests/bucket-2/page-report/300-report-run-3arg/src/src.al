/// Source objects for Report.Run 3-arg overload test (issue #1336).
/// BC emits Report.Run(ReportId, RequestPage, SystemPrinter) — no record argument.
report 308200 "RR3 Report"
{
    dataset
    {
        dataitem(DataLine; "RR3 Table")
        {
        }
    }
}

table 308200 "RR3 Table"
{
    fields
    {
        field(1; "No."; Code[20]) { }
    }
    keys { key(PK; "No.") { Clustered = true; } }
}

/// Exercises Report.Run with 3 arguments: (ReportId, RequestPage, SystemPrinter)
codeunit 308201 "RR3 Source"
{
    procedure CallRunThreeArgs(ReportId: Integer)
    begin
        Report.Run(ReportId, false, false);
    end;

    procedure CallRunThreeArgsRequestPage(ReportId: Integer)
    begin
        // Passing true for requestPage — still must not throw in standalone mode
        Report.Run(ReportId, true, false);
    end;
}
