/// Source objects for MediaSet method tests (issue #716).
table 122000 "MSM Table"
{
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Picture; MediaSet) { }
    }
    keys { key(PK; "No.") { Clustered = true; } }
}
