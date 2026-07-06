table 50134 "Two Key Arity Table"
{
    Caption = 'Two Key Arity Caption';
    DataClassification = ToBeClassified;

    fields
    {
        field(1; "Code 1"; Code[20])
        {
            DataClassification = ToBeClassified;
        }
        field(2; "Int 1"; Integer)
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
        key(PK; "Code 1", "Int 1")
        {
            Clustered = true;
        }
    }
}
