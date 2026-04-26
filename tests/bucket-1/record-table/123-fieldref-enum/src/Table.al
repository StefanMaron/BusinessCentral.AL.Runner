table 56230 "FE Test Item"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
        field(3; Color; Enum "FE Color") { }
        field(4; Price; Decimal) { }
        field(5; Quantity; Integer) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}
