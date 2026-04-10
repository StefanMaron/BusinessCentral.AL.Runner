table 53400 "Parent Object Table"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20])
        {
        }
        field(2; "Base Amount"; Decimal)
        {
        }
        field(3; "Category"; Option)
        {
            OptionMembers = "None","Low","High";

            trigger OnValidate()
            begin
                // Base table validate trigger — sets Base Amount based on Category
                if Rec.Category = Rec.Category::High then
                    Rec."Base Amount" := 1000
                else if Rec.Category = Rec.Category::Low then
                    Rec."Base Amount" := 100
                else
                    Rec."Base Amount" := 0;
            end;
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
