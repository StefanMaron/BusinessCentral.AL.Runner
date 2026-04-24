table 305001 "Validate No Value Table"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; Quantity; Decimal)
        {
            trigger OnValidate()
            begin
                "Validated Qty" := Quantity * 2;
            end;
        }
        field(3; "Validated Qty"; Decimal) { }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }

    trigger OnInsert()
    begin
        // This is the pattern from the issue: Validate(field) inside a trigger
        // BC compiler emits ALValidateSafe(fieldNo, NavType, NavValue) for this
        Validate(Quantity);
    end;
}
