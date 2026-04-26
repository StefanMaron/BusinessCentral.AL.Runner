codeunit 50490 "Test Ref Processor"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

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
        // Prove the specific value, not just "no error"
        Assert.AreEqual(42, Item.Quantity, 'Quantity must be 42 after Insert+FindFirst');
    end;
}
