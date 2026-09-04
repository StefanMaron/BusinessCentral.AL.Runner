/// See app.json for the #2510 regression writeup. This table's field 2 "Val" has no trigger
/// of its own -- the side effects under test are entirely driven by the event subscriber
/// codeunit "Sett Subscribers" in this same app, which must still fire after the sibling
/// "test" app's tableextension forces this table's NCLMetaTable to be evicted and rebuilt.
table 65520 "Sett T"
{
    DataClassification = SystemMetadata;

    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; "Val"; Integer) { }
        field(3; "Computed"; Integer) { }
        field(4; "InsertFlag"; Boolean) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
