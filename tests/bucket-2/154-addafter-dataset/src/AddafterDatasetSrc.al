table 59650 "ADDS Item"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Description; Text[100]) { }
        field(3; Qty; Integer) { }
        field(4; Price; Decimal) { }
    }
    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

/// Base report with a dataitem that has one column. The reportextension
/// below uses `modify(dataitem) { addafter(column) { ... } }` to insert
/// new columns after an existing one — the construct issue #510 names.
report 59650 "ADDS Base Report"
{
    dataset
    {
        dataitem(ItemRec; "ADDS Item")
        {
            column(ItemNo; "No.") { }
            column(ItemName; Description) { }
        }
    }
}

/// Second table to use as a child dataitem inserted via addafter.
table 59651 "ADDS Sub"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; ParentNo; Code[20]) { }
        field(2; Note; Text[100]) { }
    }
    keys
    {
        key(PK; ParentNo) { Clustered = true; }
    }
}

/// reportextension using `addafter(DataItem) { new_dataitem }` — inserts a
/// new (child) dataitem after an existing one. This is the dataset-level
/// addafter that BC AL grammar supports.
reportextension 59651 "ADDS Report Ext" extends "ADDS Base Report"
{
    dataset
    {
        addafter(ItemRec)
        {
            dataitem(SubRec; "ADDS Sub")
            {
                column(SubParentNo; ParentNo) { }
                column(SubNote; Note) { }
            }
        }
    }
}

/// Second reportextension — multiple child dataitems inserted via
/// a single addafter block.
reportextension 59652 "ADDS Report Ext Multi" extends "ADDS Base Report"
{
    dataset
    {
        addafter(ItemRec)
        {
            dataitem(SubA; "ADDS Sub")
            {
                column(SubAParentNo; ParentNo) { }
            }
            dataitem(SubB; "ADDS Sub")
            {
                column(SubBNote; Note) { }
            }
        }
    }
}

/// Helper codeunit with business logic exercised by the tests.
/// Proves the compilation unit containing reportextensions with addafter
/// dataset modifications compiles and codeunits alongside remain callable.
codeunit 59650 "ADDS Helper"
{
    procedure GetLabel(): Text
    begin
        exit('addafter-dataset');
    end;

    procedure LineTotal(qty: Integer; price: Decimal): Decimal
    begin
        exit(qty * price);
    end;

    procedure Quote(s: Text): Text
    begin
        exit('"' + s + '"');
    end;
}
