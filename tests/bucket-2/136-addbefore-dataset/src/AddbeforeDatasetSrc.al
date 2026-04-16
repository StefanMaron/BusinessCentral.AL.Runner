/// Table used as the dataitem source for the base report.
table 59600 "ABDS Item"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Name; Text[100]) { }
        field(3; Qty; Integer) { }
    }
    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

/// Base report with a single dataitem — the reportextension will add a column
/// before an existing column using addbefore.
report 59600 "ABDS Base Report"
{
    dataset
    {
        dataitem(ItemRec; "ABDS Item")
        {
            column(Name; Name) { }
            column(Qty; Qty) { }
        }
    }
}

/// reportextension using addbefore() in the dataset area — adds a new column
/// before the existing 'Name' column in the ItemRec dataitem.
/// This is the exact construct issue #421 says fails to compile.
reportextension 59601 "ABDS Report Ext" extends "ABDS Base Report"
{
    dataset
    {
        addbefore(Name)
        {
            column(ItemNo; "No.") { }
        }
    }
}

/// Helper codeunit with business logic exercised by the tests.
/// Proves that the compilation unit containing reportextensions with addbefore
/// in the dataset area compiles and codeunits alongside remain callable.
codeunit 59600 "ABDS Helper"
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
