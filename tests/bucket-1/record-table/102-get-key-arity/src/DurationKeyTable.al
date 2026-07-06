table 50137 "Duration Key Arity Table"
{
    Caption = 'Duration Key Arity Caption';
    DataClassification = ToBeClassified;

    fields
    {
        field(1; "Code 1"; Code[20])
        {
            DataClassification = ToBeClassified;
        }
        field(2; "Dur 1"; Duration)
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
        key(PK; "Code 1", "Dur 1")
        {
            Clustered = true;
        }
    }
}
