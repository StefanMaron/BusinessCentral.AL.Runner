codeunit 50400 "Ref Processor"
{
    procedure GetRecordId(var Item: Record "Error Map Item"): Text
    var
        RecId: RecordId;
    begin
        // Line 7 - simple logic
        Item.FindFirst();

        // Line 10 - RecordId access triggers ALRecordId error
        RecId := Item.RecordId;

        // Line 13
        exit(Format(RecId));
    end;

    procedure SumQuantities(var Item: Record "Error Map Item"): Decimal
    var
        Total: Decimal;
    begin
        // Line 21 - pure logic, no issues
        Total := 0;
        if Item.FindSet() then
            repeat
                Total += Item.Quantity;
            until Item.Next() = 0;
        exit(Total);
    end;
}
