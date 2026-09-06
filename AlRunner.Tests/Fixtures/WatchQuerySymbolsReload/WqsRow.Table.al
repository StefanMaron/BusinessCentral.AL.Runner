table 70620 "WQS Row"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; "Entry No."; Integer) { DataClassification = CustomerContent; }
        field(2; "Cust No."; Code[20]) { DataClassification = CustomerContent; }
        field(3; Amount; Decimal) { DataClassification = CustomerContent; }
    }
    keys { key(PK; "Entry No.") { Clustered = true; } }
}
