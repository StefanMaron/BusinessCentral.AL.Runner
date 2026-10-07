table 66800 "CSI Row"
{
    // DataPerCompany is AL's default (true).
    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; Value; Integer) { }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}
