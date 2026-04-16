/// Base table with a DropDown fieldgroup — the tableextension will append
/// a field to the end of this fieldgroup using addlast.
table 60300 "ALFG Item"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Name; Text[100]) { }
        field(3; Description; Text[200]) { }
    }
    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
    fieldgroups
    {
        fieldgroup(DropDown; "No.", Name) { }
    }
}

/// Table extension using addlast() inside the fieldgroups section — appends
/// Description to the end of the existing DropDown fieldgroup.
/// addlast in fieldgroups is a compile-time directive; it has no runtime effect
/// in unit-test context, so proving compilation is sufficient.
tableextension 60300 "ALFG Item Ext" extends "ALFG Item"
{
    fieldgroups
    {
        addlast(DropDown; Description)
    }
}

/// Business logic helper — proves that the compilation unit containing a
/// tableextension with addlast(fieldgroups) compiles and executes logic correctly.
codeunit 60300 "ALFG Helper"
{
    procedure GetLabel(): Text
    begin
        exit('addlast fieldgroup ok');
    end;

    procedure IsLongName(Name: Text): Boolean
    begin
        exit(StrLen(Name) > 20);
    end;

    procedure FormatItem(No: Code[20]; Name: Text): Text
    begin
        exit(No + ' - ' + Name);
    end;
}
