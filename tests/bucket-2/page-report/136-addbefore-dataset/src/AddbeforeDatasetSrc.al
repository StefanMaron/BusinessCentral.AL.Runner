/// Table used as the dataitem source for the base report.
// Renumbered from 59600 to avoid collision in new bucket layout (#1385).
table 1059600 "ABDS Item"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; Code; Code[20]) { }
        field(2; Name; Text[100]) { }
        field(3; Qty; Integer) { }
    }
    keys
    {
        key(PK; Code) { Clustered = true; }
    }
}

/// Extra table used as the dataitem inserted before ItemRec in the report extension.
table 59601 "ABDS Before"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; Tag; Code[20]) { }
        field(2; Note; Text[100]) { }
    }
    keys
    {
        key(PK; Tag) { Clustered = true; }
    }
}

/// Base report with a single dataitem — the reportextension will add a sibling
/// dataitem before ItemRec using addbefore.
report 59600 "ABDS Base Report"
{
    dataset
    {
        dataitem(ItemRec; "ABDS Item")
        {
            column(ItemCode; Code) { }
            column(ItemName; Name) { }
            column(ItemQty; Qty) { }
        }
    }
}

/// reportextension using addbefore() in the dataset area — adds a new sibling
/// dataitem before the existing 'ItemRec' dataitem.
/// addbefore(DataitemName) inserts a new top-level dataitem before the named one.
/// This is the exact construct issue #421 says fails to compile.
reportextension 59601 "ABDS Report Ext" extends "ABDS Base Report"
{
    dataset
    {
        addbefore(ItemRec)
        {
            dataitem(BeforeRec; "ABDS Before")
            {
                column(BeforeTag; Tag) { }
                column(BeforeNote; Note) { }
            }
        }
    }
}

/// Helper codeunit with business logic exercised by the tests.
/// Proves that the compilation unit containing reportextensions with addbefore
/// in the dataset area compiles and codeunits alongside remain callable.
// Renumbered from 59600 to avoid collision in new bucket layout (#1385).
codeunit 1059600 "ABDS Helper"
{
    procedure GetLabel(): Text
    begin
        exit('Addbefore Dataset Helper');
    end;

    procedure AddWithBonus(a: Integer; b: Integer): Integer
    begin
        exit(a + b + 10);
    end;

    procedure Concat(a: Text; b: Text): Text
    begin
        exit(a + ':' + b);
    end;
}
