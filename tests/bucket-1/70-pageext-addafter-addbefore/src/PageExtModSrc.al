table 58800 "PEM Product"
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

page 58800 "PEM Product Card"
{
    PageType = Card;
    SourceTable = "PEM Product";

    layout
    {
        area(Content)
        {
            field(CodeField; Rec.Code) { }
            field(DescriptionField; Rec.Description) { }
        }
    }
}

/// pageextension using addafter — adds a field after the Description field.
pageextension 58800 "PEM Product Card After" extends "PEM Product Card"
{
    layout
    {
        addafter(DescriptionField)
        {
            field(PriceField; Rec.Price) { }
        }
    }
}

/// pageextension using addbefore — adds a field before the Description field.
pageextension 58801 "PEM Product Card Before" extends "PEM Product Card"
{
    layout
    {
        addbefore(DescriptionField)
        {
            field(PriceBeforeField; Rec.Price) { }
        }
    }
}

/// A codeunit with business logic exercised by the tests.
/// Proves that compilation succeeds even though the same compilation unit
/// contains page extensions with addafter/addbefore modifications.
codeunit 58800 "PEM Product Helper"
{
    procedure CalcDiscountedPrice(Price: Decimal; DiscountPct: Decimal): Decimal
    begin
        exit(Price * (1 - DiscountPct / 100));
    end;

    procedure IsExpensive(Price: Decimal): Boolean
    begin
        exit(Price > 1000);
    end;
}
