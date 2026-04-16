enum 56100 "TFE Status"
{
    Extensible = false;

    value(0; "Draft")
    {
    }
    value(1; "Active")
    {
    }
    value(2; "Closed")
    {
    }
}

table 56100 "TFE Order"
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; "No."; Code[20])
        {
            DataClassification = ToBeClassified;
        }
        field(2; "Status"; Enum "TFE Status")
        {
            DataClassification = ToBeClassified;
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
