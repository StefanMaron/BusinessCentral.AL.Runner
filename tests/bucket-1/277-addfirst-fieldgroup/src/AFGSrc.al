/// Table + tableextension exercising addfirst in fieldgroups (issue #816).
/// addfirst/addlast in tableextension fieldgroups is valid AL syntax but caused
/// AL0104 in the runner's BC compiler. The rewriter now strips these as no-ops.

table 119000 "AFG Data"
{
    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; Name; Text[50]) { }
    }
    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
    fieldgroups
    {
        fieldgroup(DropDown; "Entry No.", Name) { }
    }
}

tableextension 119001 "AFG Data Ext" extends "AFG Data"
{
    fieldgroups
    {
        addfirst(DropDown; Name)
    }
}
