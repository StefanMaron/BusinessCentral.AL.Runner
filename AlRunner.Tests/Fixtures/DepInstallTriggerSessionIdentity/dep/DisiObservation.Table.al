// What the dependency's install trigger saw while it ran. Written inside TestExecutor's
// dep-company baseline window, so on a cache HIT it arrives from the restored snapshot instead
// of from a fresh trigger run - which is what makes the warm arm of the C# test meaningful.
table 70800 "DISI Observation"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Code"; Code[10]) { DataClassification = CustomerContent; }
        field(2; "Observed Security ID"; Guid) { DataClassification = CustomerContent; }
        field(3; "Observed User Name"; Text[50]) { DataClassification = CustomerContent; }
        field(4; "Session User Row Existed"; Boolean) { DataClassification = CustomerContent; }
        field(5; "Session User Row Name"; Text[50]) { DataClassification = CustomerContent; }
        field(6; "Nobody Row Existed"; Boolean) { DataClassification = CustomerContent; }
    }

    keys
    {
        key(PK; "Code") { Clustered = true; }
    }
}
