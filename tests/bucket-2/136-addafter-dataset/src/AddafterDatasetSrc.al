table 60100 "AADS Item"
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

/// Base report with columns — the reportextension will add a column
/// after Description using addafter in the dataset area.
report 60100 "AADS Base Report"
{
    dataset
    {
        dataitem(AADSItem; "AADS Item")
        {
            column(Description; Description) { }
        }
    }
}

/// Report extension using addafter() in the dataset area — modifies an existing
/// dataitem and adds a column after an existing column.
/// This is the construct issue #426 says fails to compile.
/// addafter in dataset is a layout directive; it has no runtime effect in
/// unit-test context, so proving compilation is sufficient.
reportextension 60101 "AADS Report Ext" extends "AADS Base Report"
{
    dataset
    {
        modify(AADSItem)
        {
            addafter(Description)
            {
                column(Quantity; Quantity) { }
            }
        }
    }
}

/// Business logic helper — proves that the compilation unit containing a
/// reportextension with addafter(dataset) compiles and executes logic correctly.
codeunit 60100 "AADS Helper"
{
    procedure GetLabel(): Text
    begin
        exit('After Dataset Helper');
    end;

    procedure IsLargeQuantity(Qty: Integer): Boolean
    begin
        exit(Qty >= 100);
    end;

    procedure FormatItem(No: Code[20]; Desc: Text): Text
    begin
        exit(No + ': ' + Desc);
    end;
}
