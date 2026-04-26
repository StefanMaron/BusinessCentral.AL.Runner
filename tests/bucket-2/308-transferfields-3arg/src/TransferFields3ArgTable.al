table 308300 "TF3Arg Record"
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
        }
        field(3; "Amount"; Decimal)
        {
            DataClassification = ToBeClassified;
            trigger OnValidate()
            begin
                if "Amount" < 0 then
                    Error('Amount must not be negative');
            end;
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
