// Base report and table needed for the report extension to compile.
report 70400 "TestReportWithColumns"
{
    DefaultLayout = RDLC;
    dataset
    {
        dataitem(TestCustomer; "Test Customer 129")
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

table 56290 "Test Customer 129"
{
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Name; Text[100]) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

codeunit 56290 "ReportExt Probe"
{
    procedure CompilationSucceeded(): Boolean
    begin
        // If we get here, the reportextension compiled successfully
        // (no CS1061 on scope class .Parent)
        exit(true);
    end;
}
