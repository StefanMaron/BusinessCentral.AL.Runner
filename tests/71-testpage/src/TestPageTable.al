table 56700 "TP Test Record"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
        field(3; Amount; Decimal) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

page 56700 "TP Test Card"
{
    PageType = Card;
    SourceTable = "TP Test Record";

    layout
    {
        area(Content)
        {
            field(NameField; Rec.Name) { }
            field(AmountField; Rec.Amount) { }
        }
    }
}
