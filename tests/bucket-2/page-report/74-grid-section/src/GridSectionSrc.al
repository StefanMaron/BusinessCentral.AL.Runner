table 59500 "GS Product"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; Code; Code[20]) { }
        field(2; Name; Text[100]) { }
        field(3; Price; Decimal) { }
        field(4; Quantity; Integer) { }
    }
    keys
    {
        key(PK; Code) { Clustered = true; }
    }
}

/// Card page containing a grid section — a multi-column layout directive.
/// Grid sections group fields into columns for display; they have no runtime
/// effect in a unit-test context. This proves grid sections do not block compilation.
page 59500 "GS Product Card"
{
    PageType = Card;
    SourceTable = "GS Product";

    layout
    {
        area(Content)
        {
            group(General)
            {
                grid(TopGrid)
                {
                    field(Code; Rec.Code) { ApplicationArea = All; }
                    field(Name; Rec.Name) { ApplicationArea = All; }
                }
                grid(PricingGrid)
                {
                    field(Price; Rec.Price) { ApplicationArea = All; }
                    field(Quantity; Rec.Quantity) { ApplicationArea = All; }
                }
            }
        }
    }
}

/// Business logic helper — proves the compilation unit containing
/// the page with grid sections compiles and executes logic correctly.
codeunit 59500 "GS Product Helper"
{
    procedure CalcTotal(Price: Decimal; Quantity: Integer): Decimal
    begin
        exit(Price * Quantity);
    end;

    procedure IsExpensive(Price: Decimal): Boolean
    begin
        exit(Price > 100);
    end;

    procedure FormatLabel(Name: Text; Quantity: Integer): Text
    begin
        if Quantity = 0 then
            exit(Name + ' (out of stock)');
        exit(Name + ' (' + Format(Quantity) + ')');
    end;
}
