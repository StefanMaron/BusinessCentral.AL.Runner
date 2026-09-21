// Bundle 2's own table -- the subject. Two fields, so an assertion can distinguish "this table
// has its real field list" from "this table answered with no fields at all", which is what a
// cached null NCLMetaTable produces downstream.
// Issue #4450.
table 66110 "MTC Second Table"
{
    Caption = 'MTC Second Table Caption';
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
