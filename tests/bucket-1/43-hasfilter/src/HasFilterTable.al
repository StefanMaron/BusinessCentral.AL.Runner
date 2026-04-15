table 54600 "HF Probe"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; "Status"; Integer) { }
        field(3; "Category"; Integer) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
