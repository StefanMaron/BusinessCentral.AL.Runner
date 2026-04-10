table 50118 "Validate Demo"
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; "Entry No."; Integer)
        {
            DataClassification = ToBeClassified;
        }
        field(2; "Name"; Text[100])
        {
            DataClassification = ToBeClassified;

            trigger OnValidate()
            begin
                "Name" := UpperCase("Name");
            end;
        }
        field(3; "Quantity"; Integer)
        {
            DataClassification = ToBeClassified;

            trigger OnValidate()
            begin
                "Amount" := "Quantity" * "Unit Price";
            end;
        }
        field(4; "Unit Price"; Decimal)
        {
            DataClassification = ToBeClassified;
        }
        field(5; "Amount"; Decimal)
        {
            DataClassification = ToBeClassified;
        }
    }

    keys
    {
        key(PK; "Entry No.")
        {
            Clustered = true;
        }
    }
}
