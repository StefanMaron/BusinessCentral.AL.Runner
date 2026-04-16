/// Source table used as the dataitem for the base report.
table 59340 "ADM Item"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Name; Text[100]) { }
        field(3; Qty; Integer) { }
        field(4; Price; Decimal) { }
    }
    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

/// Base report — the reportextension adds columns to its single dataitem.
report 59340 "ADM Base Report"
{
    dataset
    {
        dataitem(ItemRec; "ADM Item")
        {
            column(ItemNo; "No.") { }
            column(ItemName; Name) { }
        }
    }
}

/// reportextension using add(DataItem) to append columns to an existing
/// dataitem. This is the exact construct issue #449 says fails to compile.
reportextension 59341 "ADM Report Ext" extends "ADM Base Report"
{
    dataset
    {
        add(ItemRec)
        {
            column(ItemQty; Qty) { }
        }
    }
}

/// reportextension using add(DataItem) with multiple columns added in one block.
reportextension 59342 "ADM Report Ext Multi" extends "ADM Base Report"
{
    dataset
    {
        add(ItemRec)
        {
            column(ItemPrice; Price) { }
            column(ItemLineTotal; Qty * Price) { }
        }
    }
}

/// Helper codeunit — proves the compilation unit containing reportextensions
/// with add() in the dataset area compiles and codeunits alongside remain callable.
codeunit 59340 "ADM Helper"
{
    procedure GetLabel(): Text
    begin
        exit('dataset-add');
    end;

    procedure LineTotal(qty: Integer; price: Decimal): Decimal
    begin
        exit(qty * price);
    end;

    procedure Brackets(s: Text): Text
    begin
        exit('{' + s + '}');
    end;
}
