codeunit 50390 "Test Item Processor"
{
    Subtype = Test;

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

        // Assert
        if Result <> 35.5 then
            Error('Expected 35.5 but got %1', Result);
    end;

    [Test]
    procedure FormatItemLine_FormatsCorrectly()
    var
        Processor: Codeunit "Item Processor";
        Result: Text;
    begin
        Result := Processor.FormatItemLine('ITEM-A', 'Widget', 10);
        if Result = '' then
            Error('Expected non-empty result');
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
        if Result <> 0 then
            Error('Expected 0 but got %1', Result);
    end;
}
