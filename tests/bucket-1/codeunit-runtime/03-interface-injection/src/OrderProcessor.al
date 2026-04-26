codeunit 50102 "Order Processor"
{
    procedure ProcessOrder(ItemNo: Code[20]; Quantity: Integer; PricingSvc: Interface "IPricing Service"): Decimal
    var
        UnitPrice: Decimal;
        Total: Decimal;
    begin
        if not PricingSvc.IsAvailable(ItemNo) then
            Error('Item %1 is not available', ItemNo);

        if Quantity <= 0 then
            Error('Quantity must be positive');

        UnitPrice := PricingSvc.GetPrice(ItemNo);
        Total := UnitPrice * Quantity;

        // Apply bulk discount: 10% off for 10+ items
        if Quantity >= 10 then
            Total := Total * 0.9;

        exit(Total);
    end;
}
