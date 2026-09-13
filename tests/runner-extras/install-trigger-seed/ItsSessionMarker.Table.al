// Rows: INSTALL-RESULT, written by the install trigger with StartSession's outcome, and
// FROM-INSTALL, written only by "ITS Session Worker" (which must not run from install: #3292).
// Deliberately a different table from "Install Seed": the baseline isolation tests next door
// count that table's rows exactly.
table 60714 "ITS Session Marker"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Code"; Code[20]) { }
        field(2; "Value"; Integer) { }
    }

    keys
    {
        key(PK; "Code") { Clustered = true; }
    }
}
