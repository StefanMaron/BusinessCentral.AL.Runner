table 70541 "MTD Line"
{
    DataClassification = SystemMetadata;

    fields
    {
        field(1; "No."; Code[20]) { DataClassification = SystemMetadata; }
        field(2; "Line No."; Integer) { DataClassification = SystemMetadata; }
    }

    keys { key(PK; "No.", "Line No.") { Clustered = true; } }
}
