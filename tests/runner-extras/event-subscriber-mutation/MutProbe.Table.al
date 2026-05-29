/// <summary>
/// A table with a "Trigger Field" (validated by the test) and a "Target Field"
/// that a subscriber mutates from inside OnAfterValidateEvent. Mirrors the real
/// BaseApp pattern (e.g. Purchase Header "Vendor Cr. Memo No." OnAfterValidate
/// writing "Posting Description") that an ISV relies on.
/// </summary>
table 60200 "Mut Probe ESM"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; "Trigger Field"; Code[20]) { }
        field(3; "Target Field"; Code[50]) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
