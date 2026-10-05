// The host's own "Line No." is the decoy a link resolved against the host would read.
page 70962 "PF Card"
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
        area(FactBoxes)
        {
            part(Detail; "PF Detail Part")
            {
                ApplicationArea = All;
                Provider = Lines;
                SubPageLink = "Line Ref" = field("Line No.");
            }
        }
    }
}
