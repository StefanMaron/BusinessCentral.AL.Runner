table 54510 "SalesLine Stub"
{
    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; "Amount"; Decimal) { }
        field(3; "Description"; Text[100]) { }
        field(4; "Quantity"; Integer) { }
    }
    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}
