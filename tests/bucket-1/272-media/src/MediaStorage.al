/// Temporary table for Media test stream setup.
table 84407 "Media Test Storage"
{
    TableType = Temporary;

    fields
    {
        field(1; Id; Integer) { }
        field(2; Data; Blob) { }
    }

    keys
    {
        key(PK; Id) { }
    }
}
