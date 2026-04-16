/// Table used by the GetRangeMin / GetRangeMax proving tests.
table 60000 "GRM Item"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; Code; Code[20]) { }
        field(2; Quantity; Integer) { }
        field(3; Price; Decimal) { }
    }
    keys
    {
        key(PK; Code) { Clustered = true; }
    }
}

/// Helper codeunit that wraps SetRange + GetRangeMin / GetRangeMax so the
/// test can call them without constructing a Record inline.
codeunit 60000 "GRM Helper"
{
    /// Set an integer range filter on Quantity and return the lower bound.
    procedure GetMinQty(minQ: Integer; maxQ: Integer): Integer
    var
        Item: Record "GRM Item";
    begin
        Item.SetRange(Quantity, minQ, maxQ);
        exit(Item.GetRangeMin(Quantity));
    end;

    /// Set an integer range filter on Quantity and return the upper bound.
    procedure GetMaxQty(minQ: Integer; maxQ: Integer): Integer
    var
        Item: Record "GRM Item";
    begin
        Item.SetRange(Quantity, minQ, maxQ);
        exit(Item.GetRangeMax(Quantity));
    end;

    /// Set a decimal range on Price and return the lower bound.
    procedure GetMinPrice(lo: Decimal; hi: Decimal): Decimal
    var
        Item: Record "GRM Item";
    begin
        Item.SetRange(Price, lo, hi);
        exit(Item.GetRangeMin(Price));
    end;

    /// Set a decimal range on Price and return the upper bound.
    procedure GetMaxPrice(lo: Decimal; hi: Decimal): Decimal
    var
        Item: Record "GRM Item";
    begin
        Item.SetRange(Price, lo, hi);
        exit(Item.GetRangeMax(Price));
    end;

    /// Helper so tests can verify that a plain AddWithBonus calculation still works —
    /// proves the compilation unit is live, not a stub.
    procedure AddWithBonus(a: Integer; b: Integer): Integer
    begin
        exit(a + b + 1);
    end;
}
