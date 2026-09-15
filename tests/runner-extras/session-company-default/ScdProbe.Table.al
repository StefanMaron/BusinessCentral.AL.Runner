table 62220 "SCD Probe"
{
    DataClassification = SystemMetadata;

    fields
    {
        field(1; "Key"; Integer) { }
    }

    keys
    {
        key(PK; "Key") { Clustered = true; }
    }
}
