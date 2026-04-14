codeunit 50100 "Discount Calculator"
{
    procedure ApplyDiscount(OriginalPrice: Decimal; DiscountPercent: Decimal): Decimal
    begin
        if DiscountPercent < 0 then
            Error('Discount percentage must not be negative');
        if DiscountPercent > 100 then
            Error('Discount percentage must not exceed 100');
        exit(OriginalPrice * (1 - DiscountPercent / 100));
    end;

    procedure CalculateVAT(NetAmount: Decimal; VATPercent: Decimal): Decimal
    begin
        exit(NetAmount * VATPercent / 100);
    end;

    procedure RoundToNearest(Value: Decimal; Precision: Decimal): Decimal
    begin
        if Precision = 0 then
            exit(Value);
        exit(Round(Value / Precision, 1) * Precision);
    end;
}
