// One row per firing of the Detail part's OnAfterGetRecord.
table 70963 "PF Log"
{
    fields
    {
        field(1; "Entry No."; Integer) { AutoIncrement = true; }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}
