table 70301 "FTR Row"
{
    DataClassification = SystemMetadata;

    fields
    {
        field(1; "Entry No."; Integer) { DataClassification = SystemMetadata; }
        field(2; Name; Text[50]) { DataClassification = SystemMetadata; }
    }

    keys { key(PK; "Entry No.") { Clustered = true; } }
}
