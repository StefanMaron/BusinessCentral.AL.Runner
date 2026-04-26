/// Source objects for ReportInstance.ShowOutput and CreateTotals(Decimal,Decimal) tests (issue #1379).

/// Minimal table backing the report dataset.
table 310100 "RSO Table"
{
    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; Amount; Decimal) { }
    }
    keys { key(PK; "Entry No.") { Clustered = true; } }
}

/// Report that exercises CurrReport.ShowOutput(Boolean) and
/// CurrReport.CreateTotals(Decimal, Decimal) in triggers.
report 310100 "RSO Report"
{
    dataset
    {
        dataitem(RsoData; "RSO Table")
        {
            trigger OnAfterGetRecord()
            begin
                // Both calls are no-ops in standalone mode.
                CurrReport.ShowOutput(false);
                CurrReport.CreateTotals(RsoData.Amount, RsoData.Amount);
            end;
        }
    }
}

/// Helper codeunit that exercises ShowOutput and CreateTotals(Decimal, Decimal)
/// from inside a report trigger, and also exercises them directly on a Report variable.
codeunit 310100 "RSO Source"
{
    procedure RunReport_NoThrow()
    var
        Rep: Report "RSO Report";
    begin
        Rep.UseRequestPage(false);
        Rep.Run();
    end;
}
