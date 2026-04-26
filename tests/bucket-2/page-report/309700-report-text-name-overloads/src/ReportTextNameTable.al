/// Minimal table used as a record filter for Text-name Report overload tests.
table 309700 "RptTextName Table"
{
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Amount; Decimal) { }
    }
    keys { key(PK; "No.") { Clustered = true; } }
}

/// Report that accepts RptTextName Table as a data item.
report 309700 "RptTextName Report"
{
    dataset
    {
        dataitem(DataLine; "RptTextName Table")
        {
        }
    }
}
