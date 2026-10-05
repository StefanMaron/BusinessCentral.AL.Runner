table 70962 "PF Detail"
{
    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; "Line Ref"; Integer) { }
        field(3; Info; Text[30]) { }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}
