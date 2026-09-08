// The COUNTING witness. Every existing fixture measuring OnNewRecord uses an assignment, and an
// assignment is idempotent — it says the trigger ran, never how often. One row per firing makes
// the count an Integer a test can name.
//
// A separate table because a counter kept on the line row would not survive: the platform's own
// new-record step blanks the buffer the trigger runs against, and a draft line nobody types into
// is discarded rather than saved.
table 70641 "ONC Log"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { AutoIncrement = true; }
        field(2; Source; Code[20]) { }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}
