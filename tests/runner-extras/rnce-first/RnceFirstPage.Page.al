page 66203 "Rnce First Page"
{
    PageType = List;
    SourceTable = "Rnce First Row";
    ApplicationArea = All;
    UsageCategory = Lists;
    Caption = 'Rnce First Page Caption';

    layout
    {
        area(Content)
        {
            repeater(Rows)
            {
                field("No."; Rec."No.") { ApplicationArea = All; }
                field(Description; Rec.Description) { ApplicationArea = All; }
            }
        }
    }
}
