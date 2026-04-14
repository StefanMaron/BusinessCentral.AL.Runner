table 50107 "Test Document Line"
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; "Document No."; Code[20])
        {
            DataClassification = ToBeClassified;
        }
        field(2; "Line No."; Integer)
        {
            DataClassification = ToBeClassified;
        }
        field(3; "Item No."; Code[20])
        {
            DataClassification = ToBeClassified;
        }
        field(4; "Quantity"; Integer)
        {
            DataClassification = ToBeClassified;
        }
        field(5; "Unit Price"; Decimal)
        {
            DataClassification = ToBeClassified;
        }
        field(6; "Amount"; Decimal)
        {
            DataClassification = ToBeClassified;
        }
    }

    keys
    {
        key(PK; "Document No.", "Line No.")
        {
            Clustered = true;
        }
    }
}
