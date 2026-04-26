/// Table backing the base page used by the page extension in this suite.
table 60200 "MF Item"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; Code; Code[20]) { }
        field(2; Description; Text[100]) { }
        field(3; Price; Decimal) { }
        field(4; Quantity; Integer) { }
    }
    keys
    {
        key(PK; Code) { Clustered = true; }
    }
}

/// Base page with multiple fields in the layout so the pageextension has
/// something to reposition with movefirst.
page 60200 "MF Item Card"
{
    PageType = Card;
    SourceTable = "MF Item";

    layout
    {
        area(Content)
        {
            field(CodeField; Rec.Code) { ApplicationArea = All; }
            field(DescriptionField; Rec.Description) { ApplicationArea = All; }
            field(PriceField; Rec.Price) { ApplicationArea = All; }
            field(QuantityField; Rec.Quantity) { ApplicationArea = All; }
        }
    }
}

/// pageextension using movefirst in the layout area — moves PriceField and
/// QuantityField to be first in the content area.
/// movefirst is a layout reorder directive; it has no effect on runtime data
/// but must not block compilation.
pageextension 60200 "MF Item Card Ext" extends "MF Item Card"
{
    layout
    {
        movefirst(content; PriceField, QuantityField)
    }
}

/// Business logic helper — proves that the compilation unit containing a
/// pageextension with movefirst compiles and executes logic correctly.
codeunit 60200 "MF Helper"
{
    procedure GetLabel(): Text
    begin
        exit('movefirst ok');
    end;

    procedure CalcTotal(Price: Decimal; Qty: Integer): Decimal
    begin
        exit(Price * Qty);
    end;

    procedure FormatCode(Code: Code[20]): Text
    begin
        exit('[' + Code + ']');
    end;
}
