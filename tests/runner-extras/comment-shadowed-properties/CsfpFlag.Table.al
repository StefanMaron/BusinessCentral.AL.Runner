// Mirror of the issue #1690 repro table. Every comment below is ordinary explanatory
// prose that happens to name a property the parser looks for; before the fix each one
// was captured as the property itself, and the real declaration underneath was ignored.
table 62401 "CSFP Flag Table"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Code"; Code[10])
        {
            DataClassification = CustomerContent;
        }
        field(2; Accept; Boolean)
        {
            DataClassification = CustomerContent;
            // Per-row user flag bound by a list page to an accept/skip checkbox.
            // InitValue = true: new rows default to accepted. A consumer filters on
            // Accept = true before writing anything.
            Caption = 'Accept';
            InitValue = true;
        }
        field(3; Ratio; Code[20])
        {
            DataClassification = CustomerContent;
            /* legacy note: Caption = 'Old Ratio'; kept for the migration write-up */
            // The literal below contains '//' INSIDE a string — that is text, not a
            // comment, and must survive the comment pass intact.
            Caption = 'Ratio // Net';
        }
        // field(4; Obsolete; Integer) { InitValue = 42; }  <- commented out; not a field
    }

    keys
    {
        key(PK; "Code")
        {
            Clustered = true;
        }
    }
}
