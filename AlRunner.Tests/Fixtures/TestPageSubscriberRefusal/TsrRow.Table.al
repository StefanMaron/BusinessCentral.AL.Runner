// The row the page's control deletes. `Guarded` is what the subscriber below reads to decide
// whether to refuse, so both arms of the test drive the SAME code path and differ only in the
// data — an implementation that refused (or accepted) unconditionally fails one of them.
table 70601 "TSR Row"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { DataClassification = CustomerContent; }
        field(2; Guarded; Boolean) { DataClassification = CustomerContent; }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
