/// Helper table for Media test stream setup.
table 84407 "Media Test Storage"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Data; Blob) { }
        field(3; MediaField; Media) { }
    }

    keys
    {
        key(PK; Id) { }
    }
}
