/// Base table with a DropDown fieldgroup used to test addfirst in fieldgroups.
table 61810 "AFG Base Table"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[50]) { }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
    }
    fieldgroups
    {
        fieldgroup(DropDown; Id) { }
    }
}

/// Tableextension that uses addfirst() inside a fieldgroups modification block.
/// addfirst is metadata-only and should be silently ignored at runtime.
tableextension 61811 "AFG Base Table Ext" extends "AFG Base Table"
{
    fieldgroups
    {
        addfirst(DropDown; Name)
        {
        }
    }
}

/// Helper codeunit exercised by the test.
codeunit 61812 "AFG Field Group Src"
{
    procedure GetId(Rec: Record "AFG Base Table"): Integer
    begin
        exit(Rec.Id);
    end;

    procedure GetName(Rec: Record "AFG Base Table"): Text
    begin
        exit(Rec.Name);
    end;
}
