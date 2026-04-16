/// Table used as the dataitem source for the base report.
table 59320 "ALDS Item"
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
table 59321 "ALDS Sub"
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
/// dataitem at the end using addlast.
report 59320 "ALDS Base Report"
{
    dataset
    {
        dataitem(ALDSItem; "ALDS Item")
        {
            column(ItemCode; Code) { }
            column(ItemDescription; Description) { }
            column(ItemQuantity; Quantity) { }
        }
    }
}

/// reportextension using addlast in the dataset area — adds a new child dataitem
/// as the last nested dataitem inside ALDSItem.
/// This is the exact construct issue #420 says fails to compile.
reportextension 59322 "ALDS Report Ext" extends "ALDS Base Report"
{
    dataset
    {
        addlast(ALDSItem)
        {
            dataitem(ALDSSub; "ALDS Sub")
            {
                column(SubParentCode; ParentCode) { }
                column(SubNote; Note) { }
            }
        }
    }
}

/// Helper codeunit with business logic exercised by the tests.
/// Proves that the compilation unit containing reportextensions with addlast
/// in the dataset area compiles and codeunits alongside remain callable.
codeunit 59320 "ALDS Helper"
{
    procedure GetLabel(): Text
    begin
        exit('dataset last');
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
