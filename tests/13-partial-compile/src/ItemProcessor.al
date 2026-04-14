codeunit 50300 "Item Processor"
{
    procedure SumQuantities(var StagingRec: Record "Item Staging"): Decimal
    var
        Total: Decimal;
    begin
        Total := 0;
        if StagingRec.FindSet() then
            repeat
                Total += StagingRec.Quantity;
            until StagingRec.Next() = 0;
        exit(Total);
    end;

    procedure FormatItemLine(ItemNo: Code[20]; Description: Text[100]; Qty: Decimal): Text
    begin
        exit(ItemNo + ' - ' + Description + ': ' + Format(Qty));
    end;
}
