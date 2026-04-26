table 310002 "PVB Test Record"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
    }

    keys
    {
        key(PK; Id) { Clustered = true; }
    }
}
