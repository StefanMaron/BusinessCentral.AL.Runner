/// <summary>
/// The "implementing app" table. Declares no "Tier" field yet.
/// </summary>
table 65103 "Tdd Loyalty Member"
{
    DataClassification = SystemMetadata;

    fields
    {
        field(1; "No."; Code[20]) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
