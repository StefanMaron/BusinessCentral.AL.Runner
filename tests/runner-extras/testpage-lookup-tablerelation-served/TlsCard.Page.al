// Card over "Tls Host". Every control is bare -- none declares
// trigger OnLookup(var Text: Text): Boolean -- because a control trigger takes precedence and
// the bundle's whole subject is what happens when the TableRelation is the only thing left.
page 65794 "Tls Card"
{
    PageType = Card;
    SourceTable = "Tls Host";
    ApplicationArea = All;
    UsageCategory = None;

    layout
    {
        area(Content)
        {
            field("No."; Rec."No.") { ApplicationArea = All; }
            field("Served"; Rec."Served") { ApplicationArea = All; }
            field("No Page"; Rec."No Page") { ApplicationArea = All; }
            field("No Relation"; Rec."No Relation") { ApplicationArea = All; }
            field("Filtered Code"; Rec."Filtered Code") { ApplicationArea = All; }
        }
    }
}
