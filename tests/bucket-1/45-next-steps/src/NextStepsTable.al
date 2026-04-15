table 54800 "Next Probe"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; "Status"; Integer) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
