// Bundle 1's own table. Nothing about it is under test; it exists so this bundle is a real
// app group with a table of its own, exactly like the sibling bundle.
// Issue #4450.
table 66100 "MTC First Table"
{
    Caption = 'MTC First Table Caption';
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Code"; Code[20]) { }
        field(2; "Description"; Text[50]) { }
    }

    keys
    {
        key(PK; "Code") { Clustered = true; }
    }
}
