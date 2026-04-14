table 91000 "RH Test Data"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
        field(3; Amount; Decimal) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

report 91000 "RH Test Report"
{
    dataset
    {
        dataitem(RHTestData; "RH Test Data")
        {
        }
    }
}

codeunit 91001 "RH Report Runner"
{
    procedure RunReportWithTableView(var Rec: Record "RH Test Data")
    var
        Rep: Report "RH Test Report";
    begin
        Rep.SetTableView(Rec);
        Rep.Run();
    end;

    procedure RunReportWithoutTableView()
    var
        Rep: Report "RH Test Report";
    begin
        Rep.Run();
    end;

    procedure RunRequestPageAndGetResult(): Text
    var
        Rep: Report "RH Test Report";
    begin
        exit(Rep.RunRequestPage());
    end;
}
