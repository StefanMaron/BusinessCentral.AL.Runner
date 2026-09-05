page 70402 "TNV Lines"
{
    PageType = ListPart;
    SourceTable = "TNV Line";
    ApplicationArea = All;

    layout
    {
        area(Content)
        {
            repeater(Group)
            {
                field("No."; Rec."No.") { ApplicationArea = All; }
                field(Kind; Rec.Kind) { ApplicationArea = All; }
                field("Line No."; Rec."Line No.") { ApplicationArea = All; }
                field(Descr; Rec.Descr) { ApplicationArea = All; }
                field("No. Validated"; Rec."No. Validated") { ApplicationArea = All; }
                field("Kind Validated"; Rec."Kind Validated") { ApplicationArea = All; }
                field("Line No. Validated"; Rec."Line No. Validated") { ApplicationArea = All; }
                field("Descr Validated"; Rec."Descr Validated") { ApplicationArea = All; }
                field("No. CurrFieldNo"; Rec."No. CurrFieldNo") { ApplicationArea = All; }
            }
        }
    }
}
