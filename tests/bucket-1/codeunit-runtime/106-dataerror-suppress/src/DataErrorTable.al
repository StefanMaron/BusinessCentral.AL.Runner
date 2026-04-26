// Renumbered from 59400 to avoid collision in new bucket layout (#1385).
table 1059400 "DataError Probe"
{
    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; Description; Text[50]) { }
    }
    keys { key(PK; "Entry No.") { Clustered = true; } }
}
