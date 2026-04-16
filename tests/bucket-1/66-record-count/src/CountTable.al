table 66000 "RC Test Table"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Category; Code[10]) { }
        field(3; Amount; Decimal) { }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
    }
}
