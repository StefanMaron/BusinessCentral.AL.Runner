table 53500 "Val No Val Table"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20])
        {
        }
        field(2; "Price"; Decimal)
        {
            trigger OnValidate()
            begin
                // Double the price on validate
                if Rec.Price > 0 then
                    Rec."Computed" := Rec.Price * 2;
            end;
        }
        field(3; "Computed"; Decimal)
        {
        }
    }

    keys
    {
        key(PK; "No.")
        {
            Clustered = true;
        }
    }
}
