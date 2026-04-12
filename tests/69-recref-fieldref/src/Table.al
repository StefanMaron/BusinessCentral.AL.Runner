table 56680 "RF Test Item"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
        field(3; Price; Decimal) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}
