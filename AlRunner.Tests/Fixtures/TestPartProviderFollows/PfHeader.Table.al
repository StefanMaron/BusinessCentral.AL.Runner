table 70960 "PF Header"
{
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; "Line No."; Integer) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
