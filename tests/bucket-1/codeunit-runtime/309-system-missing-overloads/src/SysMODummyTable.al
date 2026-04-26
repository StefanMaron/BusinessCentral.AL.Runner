table 309900 "Sys MO Dummy"
{
    DataClassification = SystemMetadata;

    fields
    {
        field(1; "No"; Integer)
        {
            DataClassification = SystemMetadata;
        }
    }

    keys
    {
        key(PK; "No") { Clustered = true; }
    }
}
