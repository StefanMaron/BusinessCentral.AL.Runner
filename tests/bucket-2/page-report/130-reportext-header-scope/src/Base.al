// Base report and table for testing report extension scope parent access.
// The telemetry error was: CS1061 on 'ReportExtension50500.Header_a45_OnAfterAfterGetRecord_Scope':
// does not contain a definition for 'Parent'.
// This reproduces the exact pattern: a ReportExtension modifying a dataitem named "Header".
report 57100 "TestBaseReportForExt"
{
    DefaultLayout = RDLC;
    dataset
    {
        dataitem(Header; "Test Header Ext 57100")
        {
            column(No; "No.")
            {
            }
            column(Amount; Amount)
            {
            }
        }
    }
}

table 57100 "Test Header Ext 57100"
{
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Amount; Decimal) { }
    }
    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
