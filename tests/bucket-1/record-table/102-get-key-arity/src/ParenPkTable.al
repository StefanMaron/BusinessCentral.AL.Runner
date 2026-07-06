table 50136 "Paren PK Table"
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; "Code (Main)"; Code[20])
        {
            DataClassification = ToBeClassified;
        }
        field(2; "Line No."; Integer)
        {
            DataClassification = ToBeClassified;
        }
        field(3; "Payload"; Text[50])
        {
            DataClassification = ToBeClassified;
        }
    }

    keys
    {
        key(PK; "Code (Main)", "Line No.")
        {
            Clustered = true;
        }
    }
}
