report 50258 "Report With Triggers"
{
    UsageCategory = ReportsAndAnalysis;
    ApplicationArea = All;

    dataset
    {
    }

    trigger OnPreReport()
    var
        Log: Record "Trigger Log Table";
    begin
        Log."Line No." := 1;
        Log.Event := 'PRE';
        Log.Insert();
    end;

    trigger OnPostReport()
    var
        Log: Record "Trigger Log Table";
    begin
        Log."Line No." := 2;
        Log.Event := 'POST';
        Log.Insert();
    end;
}
