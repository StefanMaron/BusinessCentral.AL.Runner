table 90000 "TPX Test Record"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
        field(3; Amount; Decimal) { }
        field(4; Active; Boolean) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

page 90000 "TPX Test Card"
{
    PageType = Card;
    SourceTable = "TPX Test Record";

    layout
    {
        area(Content)
        {
            field(NameField; Rec.Name) { }
            field(AmountField; Rec.Amount) { }
            field(ActiveField; Rec.Active) { }
            part(LinesPart; "TPX Test Card") { }
        }
    }
}
