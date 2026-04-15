table 56270 "API Test Entry"
{
    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; "Category"; Code[20]) { }
        field(3; Description; Text[100]) { }
        field(4; Amount; Decimal) { }
        field(5; Active; Boolean) { }
    }
    keys
    {
        key(PK; "Entry No.", "Category") { Clustered = true; }
    }
}
