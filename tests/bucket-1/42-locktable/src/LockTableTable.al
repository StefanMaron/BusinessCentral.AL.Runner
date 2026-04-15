table 54500 "Lock Probe"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; "Name"; Text[50]) { }
        field(3; "Amount"; Decimal) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
