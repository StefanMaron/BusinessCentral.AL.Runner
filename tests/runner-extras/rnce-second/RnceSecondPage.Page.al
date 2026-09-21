page 66223 "Rnce Second Page"
{
    PageType = List;
    SourceTable = "Rnce Second Row";
    ApplicationArea = All;
    UsageCategory = Lists;
    Caption = 'Rnce Second Page Caption';

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
