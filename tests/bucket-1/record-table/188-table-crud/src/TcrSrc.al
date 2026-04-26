/// Minimal table used by table-CRUD test suite — issue #685.
table 124000 "TCR Item"
{
    DataClassification = SystemMetadata;
    fields
    {
        field(1; "No."; Code[20]) { DataClassification = SystemMetadata; }
        field(2; Name; Text[50]) { DataClassification = SystemMetadata; }
        field(3; Quantity; Integer) { DataClassification = SystemMetadata; }
        field(4; Category; Code[10]) { DataClassification = SystemMetadata; }
    }
    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
