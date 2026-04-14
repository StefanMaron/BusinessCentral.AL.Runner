interface "IPricing Service"
{
    procedure GetPrice(ItemNo: Code[20]): Decimal;
    procedure IsAvailable(ItemNo: Code[20]): Boolean;
}
