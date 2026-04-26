// Report object with column elements in the dataset.
// The runner should compile this with Code|Navigation (no RDLC layout)
// and produce a stub class that doesn't crash at emit time.
report 70400 "TestReportWithColumns"
{
    DefaultLayout = RDLC;
    dataset
    {
        dataitem(TestCustomer; "Test Customer")
        {
            column(CustomerNo; "No.")
            {
            }
            column(CustomerName; Name)
            {
            }
        }
    }
}

codeunit 50300 "Report Helper"
{
    procedure GetReportId(): Integer
    begin
        exit(Report::"TestReportWithColumns");
    end;

    procedure Add(a: Integer; b: Integer): Integer
    begin
        exit(a + b);
    end;
}

table 50300 "Test Customer"
{
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Name; Text[100]) { }
        field(3; City; Text[50]) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
