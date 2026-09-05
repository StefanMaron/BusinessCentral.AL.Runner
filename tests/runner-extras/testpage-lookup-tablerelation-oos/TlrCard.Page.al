page 65562 "Tlr Card"
{
    PageType = Card;
    SourceTable = "Tlr Row";
    ApplicationArea = All;
    UsageCategory = None;

    layout
    {
        area(Content)
        {
            field("No."; Rec."No.") { ApplicationArea = All; }
            // No OnLookup here, and none on the table field either.
            field("Relation Only"; Rec."Relation Only") { ApplicationArea = All; }
            // No OnLookup here; the TABLE field has one.
            field("Table Trigger"; Rec."Table Trigger") { ApplicationArea = All; }
            field("Control Trigger"; Rec."Control Trigger")
            {
                ApplicationArea = All;

                trigger OnLookup(var Text: Text): Boolean
                begin
                    Text := 'FROM-CONTROL';
                    exit(true);
                end;
            }
        }
    }
}
