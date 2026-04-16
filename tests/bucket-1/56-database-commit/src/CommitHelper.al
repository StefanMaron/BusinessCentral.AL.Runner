table 56400 "Commit Test Table"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Value; Text[50]) { }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
    }
}
