table 70001 "TP Fixture Line"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Header No."; Code[20]) { TableRelation = "TP Fixture Header"."No."; }
        field(2; "Line No."; Integer) { }
        field(3; Quantity; Decimal) { }
    }

    keys
    {
        key(PK; "Header No.", "Line No.") { Clustered = true; }
    }
}
