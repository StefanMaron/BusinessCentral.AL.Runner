table 1298001 "Value Invoke Record"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
        field(3; Amount; Decimal) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

page 1298001 "Value Invoke Card"
{
    PageType = Card;
    SourceTable = "Value Invoke Record";

    layout
    {
        area(Content)
        {
            field(NameField; Rec.Name) { }
            field(AmountField; Rec.Amount) { }
        }
    }
}
