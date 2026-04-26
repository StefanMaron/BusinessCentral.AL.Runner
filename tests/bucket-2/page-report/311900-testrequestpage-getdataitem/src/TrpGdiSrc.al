/// Report with data items used to exercise TestRequestPage.GetDataItem.
/// The data items expose filters that tests can set and verify via GetDataItem().
report 311900 "TRP GDI Report"
{
    Caption = 'TRP GDI Report';
    dataset
    {
        dataitem(Customer; "TRP GDI Cust")
        {
            column(CustNo; Id) { }
            dataitem(Entry; "TRP GDI Entry")
            {
                DataItemLink = CustId = field(Id);
                column(EntryAmt; Amount) { }
            }
        }
    }

    requestpage
    {
        layout
        {
            area(Content)
            {
                group(Options)
                {
                    Caption = 'Options';
                    field(FilterFld; FilterValue)
                    {
                        Caption = 'Filter';
                        ApplicationArea = All;
                    }
                }
            }
        }
    }

    var
        FilterValue: Text[50];
}

table 311901 "TRP GDI Cust"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[50]) { }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
    }
}

table 311902 "TRP GDI Entry"
{
    fields
    {
        field(1; EntryNo; Integer) { }
        field(2; CustId; Integer) { }
        field(3; Amount; Decimal) { }
    }
    keys
    {
        key(PK; EntryNo) { Clustered = true; }
    }
}

codeunit 99800 "TRP GDI Src"
{
    procedure RunReport()
    begin
        Report.Run(311900);
    end;
}
