table 50109 "Test Product"
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; "Code"; Code[20])
        {
            DataClassification = ToBeClassified;
        }
        field(2; "Description"; Text[100])
        {
            DataClassification = ToBeClassified;
        }
        field(3; "Category"; Code[20])
        {
            DataClassification = ToBeClassified;
        }
        field(4; "Price"; Decimal)
        {
            DataClassification = ToBeClassified;
        }
        field(5; "Stock"; Integer)
        {
            DataClassification = ToBeClassified;
        }
    }

    keys
    {
        key(PK; "Code")
        {
            Clustered = true;
        }
    }
}
