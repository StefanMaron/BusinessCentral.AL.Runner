table 70920 "ILP Observation"
{
    DataClassification = SystemMetadata;

    fields
    {
        field(1; "Code"; Code[20]) { }
        field(2; "Load Succeeded"; Boolean) { }
        field(3; "Error Text"; Text[2048]) { }
    }

    keys
    {
        key(PK; "Code") { Clustered = true; }
    }
}
