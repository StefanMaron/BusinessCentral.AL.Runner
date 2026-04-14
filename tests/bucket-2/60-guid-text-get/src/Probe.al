table 56600 "GRT Row"
{
    fields
    {
        field(1; "Package ID"; Guid) { }
        field(2; Name; Text[50]) { }
    }
    keys { key(PK; "Package ID") { Clustered = true; } }
}

table 56601 "GRT Alert"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; UniqueIdentifier; Text[100]) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}
