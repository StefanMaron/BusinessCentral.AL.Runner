table 50120 "Demo Order"
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; "Order No."; Code[20])
        {
            DataClassification = ToBeClassified;
        }
        field(2; "Status"; Enum "Order Status")
        {
            DataClassification = ToBeClassified;
        }
        field(3; "Description"; Text[100])
        {
            DataClassification = ToBeClassified;
        }
    }

    keys
    {
        key(PK; "Order No.")
        {
            Clustered = true;
        }
    }
}
