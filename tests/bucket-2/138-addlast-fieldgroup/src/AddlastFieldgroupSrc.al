/// Base table with a fieldgroup — the tableextension will append to it using addlast.
table 50138 "ALFG Item"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Name; Text[100]) { }
        field(3; Description; Text[200]) { }
        field(4; Quantity; Integer) { }
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

/// tableextension using addlast inside fieldgroups — appends Description to
/// the DropDown fieldgroup. This is the construct issue #443 says fails.
tableextension 50139 "ALFG Item Ext" extends "ALFG Item"
{
    fieldgroups
    {
        addlast(DropDown; Description)
    }
}

/// Helper codeunit with business logic in the same compilation unit as the
/// tableextension. Proves the unit compiles and codeunit logic is callable.
codeunit 50138 "ALFG Helper"
{
    procedure GetLabel(): Text
    begin
        exit('fieldgroup last');
    end;

    procedure AddWithBonus(a: Integer; b: Integer): Integer
    begin
        exit(a + b + 1);
    end;

    procedure Concat(a: Text; b: Text): Text
    begin
        exit(a + '|' + b);
    end;
}
