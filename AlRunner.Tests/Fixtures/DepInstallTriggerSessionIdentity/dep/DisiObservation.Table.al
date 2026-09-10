// What the dependency's install trigger saw while it ran. Written inside TestExecutor's
// dep-company baseline window, so on a cache HIT it arrives from the restored snapshot instead
// of from a fresh trigger run - which is what makes the warm arm of the C# test meaningful.
//
// Fields 7..13 were added by AlRunner#3757: the same question one table further out. The
// Company row (2000000006), the Access Control SUPER row (2000000053) and the Published
// Application / NAV App Installed App rows (2000000206 / 2000000153) were all seeded AFTER
// this trigger ran, so install code observed none of them.
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
        field(7; "Company Row Existed"; Boolean) { DataClassification = CustomerContent; }
        field(8; "Company Row Name"; Text[30]) { DataClassification = CustomerContent; }
        field(9; "Other Company Row Existed"; Boolean) { DataClassification = CustomerContent; }
        field(10; "Super Row Existed"; Boolean) { DataClassification = CustomerContent; }
        field(11; "Nobody Super Row Existed"; Boolean) { DataClassification = CustomerContent; }
        field(12; "Own App Installed Row Existed"; Boolean) { DataClassification = CustomerContent; }
        field(13; "Bundle App Installed Row Existed"; Boolean) { DataClassification = CustomerContent; }
    }

    keys
    {
        key(PK; "Code") { Clustered = true; }
    }
}
