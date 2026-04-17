/// Helper table providing a Blob field for InStream creation in tests.
table 107000 "SMisc Blob"
{
    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; Content; Blob) { }
    }
    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}
