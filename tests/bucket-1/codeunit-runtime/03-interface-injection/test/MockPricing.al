codeunit 50103 "Mock Pricing Service" implements "IPricing Service"
{
    var
        MockPrice: Decimal;
        MockAvailable: Boolean;

    procedure SetMockPrice(Price: Decimal)
    begin
        MockPrice := Price;
    end;

    procedure SetMockAvailable(Available: Boolean)
    begin
        MockAvailable := Available;
    end;

    procedure GetPrice(ItemNo: Code[20]): Decimal
    begin
        exit(MockPrice);
    end;

    procedure IsAvailable(ItemNo: Code[20]): Boolean
    begin
        exit(MockAvailable);
    end;
}
