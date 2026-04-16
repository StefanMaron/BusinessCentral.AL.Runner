report 50258 "Report With Triggers"
{
    UsageCategory = ReportsAndAnalysis;
    ApplicationArea = All;

    dataset
    {
    }

    trigger OnPreReport()
    var
        State: Codeunit "Report Trigger State";
    begin
        State.SetPreFired();
    end;

    trigger OnPostReport()
    var
        State: Codeunit "Report Trigger State";
    begin
        State.SetPostFired();
    end;
}
