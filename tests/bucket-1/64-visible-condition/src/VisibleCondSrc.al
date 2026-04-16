/// Table backing the page used to test conditional Visible attributes.
table 81200 "VCond Item"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
        field(3; Amount; Decimal) { }
        field(4; Active; Boolean) { }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
    }
}

/// Page that uses conditional Visible on several fields.
/// Tests that expressions (variable, record field comparison) compile correctly.
page 81200 "VCond Item Page"
{
    PageType = Card;
    SourceTable = "VCond Item";

    layout
    {
        area(Content)
        {
            group(General)
            {
                field(IdField; Rec.Id) { }
                field(NameField; Rec.Name)
                {
                    // Conditional Visible using a page-level boolean variable
                    Visible = ShowDetails;
                }
                field(AmountField; Rec.Amount)
                {
                    // Conditional Visible using a record field expression
                    Visible = Rec.Active;
                }
                field(ActiveField; Rec.Active)
                {
                    // Conditional Visible using a compound expression
                    Visible = Rec.Amount > 0;
                }
            }
        }
    }

    var
        ShowDetails: Boolean;

    procedure SetShowDetails(Show: Boolean)
    begin
        ShowDetails := Show;
    end;
}
