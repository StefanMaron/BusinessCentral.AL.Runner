/// Table backing the base page used by the page extension in this suite.
table 50836 "MBF Product"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; Code; Code[20]) { }
        field(2; Description; Text[100]) { }
        field(3; Price; Decimal) { }
    }
    keys
    {
        key(PK; Code) { Clustered = true; }
    }
}

/// Base page with multiple fields in the layout so the pageextension has
/// something to reposition with movebefore.
page 50836 "MBF Product Card"
{
    PageType = Card;
    SourceTable = "MBF Product";

    layout
    {
        area(Content)
        {
            field(CodeField; Rec.Code) { ApplicationArea = All; }
            field(DescriptionField; Rec.Description) { ApplicationArea = All; }
            field(PriceField; Rec.Price) { ApplicationArea = All; }
        }
    }
}

/// pageextension using movebefore in the layout area — moves PriceField before
/// DescriptionField. movebefore is a layout reorder directive; it has no effect
/// on runtime data but must not block compilation.
pageextension 50836 "MBF Product Card Ext" extends "MBF Product Card"
{
    layout
    {
        movebefore(DescriptionField; PriceField)
    }
}

/// Helper codeunit with business logic exercised by the tests.
/// Proves that the compilation unit containing pageextensions with movebefore
/// compiles and codeunits defined alongside remain callable.
codeunit 50836 "MBF Product Helper"
{
    procedure GetLabel(): Text
    begin
        exit('movebefore ok');
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
