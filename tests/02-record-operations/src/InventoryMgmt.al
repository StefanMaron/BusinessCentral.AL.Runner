codeunit 50101 "Inventory Management"
{
    procedure AddStock(ItemNo: Code[20]; Qty: Integer)
    var
        Item: Record "Sample Item";
    begin
        Item.Get(ItemNo);
        Item."Inventory" := Item."Inventory" + Qty;
        Item.Modify();
    end;

    procedure RemoveStock(ItemNo: Code[20]; Qty: Integer)
    var
        Item: Record "Sample Item";
    begin
        Item.Get(ItemNo);
        if Item."Inventory" < Qty then
            Error('Insufficient inventory for item %1. Available: %2, Requested: %3',
                ItemNo, Item."Inventory", Qty);
        Item."Inventory" := Item."Inventory" - Qty;
        Item.Modify();
    end;

    procedure CalculateOrderTotal(OrderNo: Code[20]): Decimal
    var
        OrderLine: Record "Sample Order Line";
        Total: Decimal;
    begin
        OrderLine.SetRange("Order No.", OrderNo);
        if OrderLine.FindSet() then
            repeat
                Total += OrderLine."Line Amount";
            until OrderLine.Next() = 0;
        exit(Total);
    end;
}
