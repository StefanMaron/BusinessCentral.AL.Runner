// Renumbered from 50109 to avoid collision in new bucket layout (#1385).
table 1050109 "Key Probe"
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; "Code"; Code[20])
        {
            DataClassification = ToBeClassified;
        }
        field(2; "Name"; Text[100])
        {
            DataClassification = ToBeClassified;
        }
        field(3; "Sequence"; Integer)
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
        key(ByName; "Name")
        {
        }
        key(BySeq; "Sequence")
        {
        }
    }
}
