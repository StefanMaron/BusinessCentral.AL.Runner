table 56500 "IR Test"
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; "No."; Code[20])
        {
            DataClassification = ToBeClassified;
        }
        field(2; "Name"; Text[100])
        {
            DataClassification = ToBeClassified;
        }
        field(3; "Amount"; Decimal)
        {
            DataClassification = ToBeClassified;
        }
        field(4; "Qty"; Integer)
        {
            DataClassification = ToBeClassified;
        }
        field(5; "Active"; Boolean)
        {
            DataClassification = ToBeClassified;
        }
        field(6; "Score"; Integer)
        {
            DataClassification = ToBeClassified;
            InitValue = 5;
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
