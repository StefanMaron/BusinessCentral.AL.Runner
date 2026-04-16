table 59950 "Strict Test Item"
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

query 59950 "Strict Test Query"
{
    elements
    {
        dataitem(Item; "Strict Test Item")
        {
            column(Id; Id) { }
            column(Name; Name) { }
        }
    }
}
