// A part with a SubPageLink and no Provider, which no other part names as one.
page 70963 "PF Plain Card"
{
    PageType = Card;
    SourceTable = "PF Header";
    ApplicationArea = All;
    UsageCategory = None;

    layout
    {
        area(Content)
        {
            field("No."; Rec."No.") { ApplicationArea = All; }
            part(Lines; "PF Lines Part")
            {
                ApplicationArea = All;
                SubPageLink = "Header No." = field("No.");
            }
        }
    }
}
