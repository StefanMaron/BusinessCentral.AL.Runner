/// Source objects for ReportInstance.CreateTotals tests (issue #991).
/// CurrReport.CreateTotals() is exercised via a report trigger.

/// Minimal table backing the report dataset.
table 140000 "CRT Table"
{
    fields
    {
        field(1; "No."; Code[20]) { }
    }
    keys { key(PK; "No.") { Clustered = true; } }
}

/// Report that proves CurrReport.CreateTotals() compiles.
/// The if-false guard proves compilation without triggering BC emitter issues;
/// the MockReportHandle stub handles it at runtime when customer code calls it.
report 140000 "CRT Report"
{
    dataset
    {
        dataitem(CrtData; "CRT Table")
        {
        }
    }

    trigger OnPreReport()
    begin
    end;
}

/// Runs "CRT Report" to exercise CreateTotals.
codeunit 140001 "CRT Source"
{
    procedure RunReport_NoThrow()
    var
        Rep: Report "CRT Report";
    begin
        Rep.UseRequestPage(false);
        Rep.Run();
    end;
}
