table 1316001 "FER Log"
{
    DataClassification = SystemMetadata;
    fields
    {
        field(1; PK; Integer) { }
        field(2; ActionType; Enum "FER My Action") { }
    }
    keys
    {
        key(K; PK) { Clustered = true; }
    }
}
