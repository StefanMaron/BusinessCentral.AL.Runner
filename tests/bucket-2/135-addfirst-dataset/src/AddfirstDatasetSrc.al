/// Table used as the dataitem source for the base report.
table 59300 "AFDS Item"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; Code; Code[20]) { }
        field(2; Description; Text[100]) { }
        field(3; Quantity; Integer) { }
    }
    keys
    {
        key(PK; Code) { Clustered = true; }
    }
}

/// Extra table used as a nested child dataitem in the report extension.
table 59301 "AFDS Sub"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; ParentCode; Code[20]) { }
        field(2; Note; Text[100]) { }
    }
    keys
    {
        key(PK; ParentCode) { Clustered = true; }
    }
}

/// Base report with a single dataitem — the reportextension will add a child
/// dataitem at the beginning using addfirst.
report 59300 "AFDS Base Report"
{
    dataset
    {
        dataitem(AFDSItem; "AFDS Item")
        {
            column(ItemCode; Code) { }
            column(ItemDescription; Description) { }
            column(ItemQuantity; Quantity) { }
        }
    }
}

/// reportextension using addfirst in the dataset area — adds a new child dataitem
/// as the first nested dataitem inside AFDSItem.
/// This is the exact construct issue #416 says fails to compile.
reportextension 59302 "AFDS Report Ext" extends "AFDS Base Report"
{
    dataset
    {
        addfirst(AFDSItem)
        {
            dataitem(AFDSSub; "AFDS Sub")
            {
                column(SubParentCode; ParentCode) { }
                column(SubNote; Note) { }
            }
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
