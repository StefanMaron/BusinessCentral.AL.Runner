// One part, linked on the line table's first primary-key field — the shape of every document
// card in the Base Application.
page 70645 "ONC Card"
{
    PageType = Card;
    SourceTable = "ONC Header";
    ApplicationArea = All;
    UsageCategory = Administration;

    layout
    {
        area(Content)
        {
            field("No."; Rec."No.") { ApplicationArea = All; }
            part(Lines; "ONC Lines")
            {
                ApplicationArea = All;
                SubPageLink = "Header No." = field("No.");
            }
        }
    }
}
