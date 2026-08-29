table 64530 "Qja Order"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; "Cust No."; Code[20]) { }
        field(3; Amount; Decimal) { }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}
