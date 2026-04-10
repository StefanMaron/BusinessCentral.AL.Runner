codeunit 50490 "Test Ref Processor"
{
    Subtype = Test;

    [Test]
    procedure SumQuantities_Works()
    var
        Item: Record "Error Map Item";
        Proc: Codeunit "Ref Processor";
        Result: Decimal;
    begin
        // The Ref Processor codeunit has GetRecordId which uses
        // RecordId (unsupported → ALRecordId error). The codeunit
        // should be excluded but this test on the table itself passes.
        Item.Init();
        Item."Entry No." := 1;
        Item."Item No." := 'A';
        Item.Quantity := 42;
        Item.Insert(false);

        Item.Reset();
        Item.FindFirst();
        if Item.Quantity <> 42 then
            Error('Expected 42 but got %1', Item.Quantity);
    end;
}
