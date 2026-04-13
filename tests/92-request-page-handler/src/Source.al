table 92000 "RPH Test Data"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

report 92000 "RPH Test Report"
{
    dataset
    {
        dataitem(RPHTestData; "RPH Test Data")
        {
        }
    }
}

codeunit 92001 "RPH Report Caller"
{
    procedure CallRunRequestPage(): Text
    var
        Rep: Report "RPH Test Report";
    begin
        exit(Rep.RunRequestPage());
    end;
}
