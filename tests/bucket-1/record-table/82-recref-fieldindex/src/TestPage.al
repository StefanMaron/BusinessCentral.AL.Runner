page 50400 "Test Item Card"
{
    PageType = Card;
    SourceTable = "Test Item";

    layout
    {
        area(Content)
        {
            field("No."; Rec."No.") { }
            field(Description; Rec.Description) { }
            field(Amount; Rec.Amount) { }
        }
    }
}
