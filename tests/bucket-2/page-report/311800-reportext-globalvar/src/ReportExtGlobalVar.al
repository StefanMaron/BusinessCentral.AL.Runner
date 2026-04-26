// Base report and table for testing ReportExtension GetGlobalVariable/SetGlobalVariable.
// Issue #1450: BC emits GetGlobalVariable/SetGlobalVariable on scope classes that access
// global variables declared on a reportextension. After stripping NavReportExtension,
// these methods must be present on ReportExtension<N> or compilation fails with CS1061.
report 311800 "REGV Base Report"
{
    DefaultLayout = RDLC;
    dataset
    {
        dataitem(Customer; "REGV Customer")
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

table 311800 "REGV Customer"
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

// Report extension with a global variable accessed from a modify trigger.
// BC generates scope classes that call _parent.GetGlobalVariable(id, type) /
// _parent.SetGlobalVariable(id, type, value) to read/write global vars declared
// on the reportextension. This requires these methods to exist on ReportExtension<N>.
reportextension 311800 "REGV Report Ext" extends "REGV Base Report"
{
    dataset
    {
        modify(Customer)
        {
            trigger OnAfterAfterGetRecord()
            begin
                // Accessing RowCount causes BC to emit GetGlobalVariable/SetGlobalVariable
                // calls in the generated scope class for this trigger.
                RowCount += 1;
            end;
        }
    }

    var
        RowCount: Integer;

    procedure GetRowCount(): Integer
    begin
        exit(RowCount);
    end;
}
