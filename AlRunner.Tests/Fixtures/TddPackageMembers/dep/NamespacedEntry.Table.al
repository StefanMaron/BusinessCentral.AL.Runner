namespace Loyalty.Data;

table 65383 "Namespaced Entry"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; "Existing Points"; Integer) { }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
    }
}
