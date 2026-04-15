// Single-field PK table for SetRecFilter tests
table 55500 "SRF Single Table"
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; Code; Code[20])
        {
            DataClassification = ToBeClassified;
        }
        field(2; Description; Text[100])
        {
            DataClassification = ToBeClassified;
        }
    }

    keys
    {
        key(PK; Code)
        {
            Clustered = true;
        }
    }
}

// Composite-PK table for SetRecFilter tests
table 55501 "SRF Composite Table"
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; "Doc Type"; Integer)
        {
            DataClassification = ToBeClassified;
        }
        field(2; "Doc No."; Code[20])
        {
            DataClassification = ToBeClassified;
        }
        field(3; "Line No."; Integer)
        {
            DataClassification = ToBeClassified;
        }
        field(4; Description; Text[100])
        {
            DataClassification = ToBeClassified;
        }
    }

    keys
    {
        key(PK; "Doc Type", "Doc No.", "Line No.")
        {
            Clustered = true;
        }
    }
}
