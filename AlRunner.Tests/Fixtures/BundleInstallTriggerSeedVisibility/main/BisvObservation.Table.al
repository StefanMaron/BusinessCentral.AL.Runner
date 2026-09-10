// What the bundle's OWN install trigger saw while it ran. Per-app-group by construction: this
// app is the bundle under test, so nothing here is carried by the #1867 dependency+company
// baseline snapshot, whose key covers the dependency set and not the bundle.
table 70840 "BISV Observation"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Code"; Code[10]) { DataClassification = CustomerContent; }
        field(2; "Company Row Existed"; Boolean) { DataClassification = CustomerContent; }
        field(3; "Company Row Name"; Text[30]) { DataClassification = CustomerContent; }
        field(4; "Other Company Row Existed"; Boolean) { DataClassification = CustomerContent; }
        field(5; "Super Row Existed"; Boolean) { DataClassification = CustomerContent; }
        field(6; "Nobody Super Row Existed"; Boolean) { DataClassification = CustomerContent; }
        field(7; "Own Published App Row Existed"; Boolean) { DataClassification = CustomerContent; }
        field(8; "Other Published App Row Existed"; Boolean) { DataClassification = CustomerContent; }
        field(10; "Observed Security ID"; Guid) { DataClassification = CustomerContent; }
    }

    keys
    {
        key(PK; "Code") { Clustered = true; }
    }
}
