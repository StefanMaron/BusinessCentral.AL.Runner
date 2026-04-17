codeunit 50390 "Test Item Processor"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure SumQuantities_ReturnsCorrectTotal()
    var
        Staging: Record "Item Staging";
        Processor: Codeunit "Item Processor";
        Result: Decimal;
    begin
        // Arrange
        Staging.Init();
        Staging."Entry No." := 1;
        Staging."Item No." := 'ITEM-A';
        Staging.Quantity := 10;
        Staging.Insert(false);

        Staging.Init();
        Staging."Entry No." := 2;
        Staging."Item No." := 'ITEM-B';
        Staging.Quantity := 25.5;
        Staging.Insert(false);

        Staging.Reset();

        // Act
        Result := Processor.SumQuantities(Staging);

        // Assert — specific value, not just "no error"
        Assert.AreEqual(35.5, Result, 'SumQuantities(10 + 25.5) must return 35.5');
    end;

    [Test]
    procedure FormatItemLine_FormatsCorrectly()
    var
        Processor: Codeunit "Item Processor";
        Result: Text;
    begin
        Result := Processor.FormatItemLine('ITEM-A', 'Widget', 10);
        // Prove the exact format, not just non-empty
        Assert.AreEqual('ITEM-A - Widget: 10', Result, 'FormatItemLine must produce "ITEM-A - Widget: 10"');
    end;

    [Test]
    procedure SumQuantities_EmptyTable_ReturnsZero()
    var
        Staging: Record "Item Staging";
        Processor: Codeunit "Item Processor";
        Result: Decimal;
    begin
        Staging.Reset();
        Result := Processor.SumQuantities(Staging);
        Assert.AreEqual(0, Result, 'SumQuantities on empty table must return 0');
    end;
}
