page 500000 "Repro SetValue Card"
{
    PageType = Card;
    SourceTable = "Repro SetValue Tab";
    Editable = true;

    layout
    {
        area(content)
        {
            field("No."; Rec."No.") { ApplicationArea = All; }
            field(Description; Rec.Description) { ApplicationArea = All; }
        }
    }
}
