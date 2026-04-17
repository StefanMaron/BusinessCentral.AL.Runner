/// Report with a request page used to exercise TestRequestPage methods.
report 84402 "TRM Report"
{
    Caption = 'TRM Report';
    dataset { }

    requestpage
    {
        layout
        {
            area(Content)
            {
                group(Options)
                {
                    Caption = 'Options';
                    field(AmountFld; AmountValue)
                    {
                        Caption = 'Amount';
                        ApplicationArea = All;
                    }
                }
            }
        }
    }

    var
        AmountValue: Decimal;
}

table 84403 "TRM Nav Rec"
{
    fields
    {
        field(1; Id; Integer) { }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
    }
}

codeunit 84400 "TRM Src"
{
    procedure RunReport()
    begin
        Report.Run(84402);
    end;
}
