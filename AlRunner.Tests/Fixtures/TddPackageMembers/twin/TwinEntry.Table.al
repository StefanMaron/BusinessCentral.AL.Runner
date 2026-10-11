namespace Other.Data;

table 65390 "Namespaced Entry"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; "Twin Points"; Integer) { }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
    }
}
