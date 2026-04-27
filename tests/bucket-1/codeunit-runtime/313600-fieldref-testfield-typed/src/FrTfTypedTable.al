/// Table fixture for FieldRef.TestField typed overload tests (issue #1400).
table 313600 "FrTf Rec"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
        field(3; Qty; Integer) { }
        field(4; Price; Decimal) { }
        field(5; Active; Boolean) { }
        field(6; PostedOn; Date) { }
        field(7; PostedAt; DateTime) { }
        field(8; Code; Code[20]) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}
