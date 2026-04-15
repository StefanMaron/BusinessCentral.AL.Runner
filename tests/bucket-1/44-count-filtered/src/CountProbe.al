table 54700 "Count Probe"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; "Status"; Integer) { }
        field(3; "Amount"; Decimal) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
