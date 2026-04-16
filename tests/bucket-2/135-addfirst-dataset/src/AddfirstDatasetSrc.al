/// Table used as the dataitem source for the base report.
table 59300 "AFDS Item"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Description; Text[100]) { }
        field(3; Quantity; Integer) { }
    }
    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

/// Base report with a dataset — reportextension will prepend columns using addfirst.
report 59300 "AFDS Base Report"
{
    dataset
    {
        dataitem(AFDSItem; "AFDS Item")
        {
            column(Description; Description) { }
            column(Quantity; Quantity) { }
        }
    }
}

/// reportextension using addfirst in the dataset area (prepend a column).
/// This is the exact construct issue #416 says fails to compile.
reportextension 59301 "AFDS Report Ext" extends "AFDS Base Report"
{
    dataset
    {
        addfirst(AFDSItem)
        {
            column(ItemNo; "No.") { }
        }
    }
}

/// Helper codeunit with business logic exercised by the tests.
/// Proves that the compilation unit containing reportextensions with addfirst
/// in the dataset area compiles and codeunits alongside remain callable.
codeunit 59300 "AFDS Helper"
{
    procedure GetLabel(): Text
    begin
        exit('dataset first');
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
