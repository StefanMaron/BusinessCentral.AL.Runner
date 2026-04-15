table 56241 "Temp Test Item"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[50]) { }
        field(3; Amount; Decimal) { }
    }

    keys
    {
        key(PK; Id)
        {
            Clustered = true;
        }
    }
}
