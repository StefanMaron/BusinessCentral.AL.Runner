/// Source objects for ReportInstance.CreateTotals stub tests (issue #991).
///
/// NOTE: CurrReport.CreateTotals() cannot appear in the runner's own AL test
/// suite because the BC compiler emitter crashes (producing zero C# output for
/// the entire compilation unit) when any code path references CreateTotals —
/// even inside an "if false then" dead-code block.  The MockReportHandle stubs
/// exist for customer reports that call CreateTotals; those customer reports
/// compile and run correctly through the runner pipeline.  See issue #991 for
/// the CS1061 root cause.

/// Minimal table backing the report dataset.
table 140000 "CRT Table"
{
    fields
    {
        field(1; "No."; Code[20]) { }
    }
    keys { key(PK; "No.") { Clustered = true; } }
}

/// Minimal report that exercises the report-run path backed by MockReportHandle.
/// Customer code with CurrReport.CreateTotals() in this trigger would require the
/// CreateTotals stub; the stub is verified to exist by the passing CI build.
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

/// Runs "CRT Report" via MockReportHandle to confirm no-throw.
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
