/// See app.json for the #2463 regression writeup. This table's field 2 OnValidate trigger
/// is the thing under test: its side effect (writing field 3) must still run after the
/// sibling "test" app's tableextension forces this table's NCLMetaTable to be evicted and
/// rebuilt.
table 65270 "Tefte T"
{
    DataClassification = SystemMetadata;

    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; "Val"; Integer)
        {
            trigger OnValidate()
            begin
                "Computed" := Val * 2;
            end;
        }
        field(3; "Computed"; Integer) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
