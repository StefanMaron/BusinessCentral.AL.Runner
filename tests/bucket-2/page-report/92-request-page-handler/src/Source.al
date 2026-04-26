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

// Renumbered from 92001 to avoid collision in new bucket layout (#1385).
codeunit 1092001 "RPH Report Caller"
{
    procedure CallRunRequestPage(): Text
    var
        Rep: Report "RPH Test Report";
    begin
        exit(Rep.RunRequestPage());
    end;
}
