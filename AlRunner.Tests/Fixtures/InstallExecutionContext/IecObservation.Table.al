table 70900 "IEC Observation"
{
    DataClassification = SystemMetadata;

    fields
    {
        field(1; "Code"; Code[20]) { }
        field(2; "Exec Ctx"; Text[30]) { }
        field(3; "Module Exec Ctx"; Text[30]) { }
    }

    keys
    {
        key(PK; "Code") { Clustered = true; }
    }
}
