tableextension 53300 "Item Extension" extends "Base Item Table"
{
    fields
    {
        field(53300; "Custom Cost"; Decimal)
        {
            trigger OnValidate()
            begin
                // When Custom Cost changes, update Category based on price
                if Rec."Custom Cost" > 100 then
                    Rec.Validate("Category", Rec."Category"::Premium)
                else
                    Rec.Validate("Category", Rec."Category"::Standard);
            end;
        }
    }
}
