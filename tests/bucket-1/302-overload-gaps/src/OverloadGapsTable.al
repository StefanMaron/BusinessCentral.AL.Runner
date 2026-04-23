// Shared table for overload-gaps test suite (issues #1180, #1183, #1187, #1192)
table 302000 "OG Table"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[50]) { }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
    }
}
