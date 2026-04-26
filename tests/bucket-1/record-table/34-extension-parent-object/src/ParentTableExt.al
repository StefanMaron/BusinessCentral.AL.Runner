tableextension 53400 "Parent Table Ext" extends "Parent Object Table"
{
    fields
    {
        field(53400; "Ext Cost"; Decimal)
        {
            trigger OnValidate()
            begin
                // Extension trigger that calls Validate on a base field
                // This generates _parent.ParentObject.ALValidateSafe(...)
                if Rec."Ext Cost" > 500 then
                    Rec.Validate(Category, Rec.Category::High)
                else
                    Rec.Validate(Category, Rec.Category::Low);
            end;
        }
    }
}
