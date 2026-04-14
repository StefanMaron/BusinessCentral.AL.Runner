table 50480 "PV Row"
{
    fields
    {
        field(1; Id; Integer) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

page 50480 "PV Probe Page"
{
    PageType = List;
    SourceTable = "PV Row";
}
