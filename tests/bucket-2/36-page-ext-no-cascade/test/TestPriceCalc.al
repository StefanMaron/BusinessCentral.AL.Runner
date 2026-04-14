codeunit 53600 "Test Price Calc"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure CalcTotalReturnsCorrectResult()
    var
        PriceRec: Record "Price Table";
        Calc: Codeunit "Price Calc";
    begin
        // Positive: CalcTotal multiplies unit price by quantity
        PriceRec.Init();
        PriceRec."No." := 'P1';
        PriceRec."Unit Price" := 25.0;
        PriceRec.Insert(true);

        PriceRec.Get('P1');
        Assert.AreEqual(75.0, Calc.CalcTotal(PriceRec, 3), 'Should be 25 * 3 = 75');
    end;

    [Test]
    procedure CalcTotalZeroQty()
    var
        PriceRec: Record "Price Table";
        Calc: Codeunit "Price Calc";
    begin
        // Negative: zero quantity gives zero total
        PriceRec.Init();
        PriceRec."No." := 'P2';
        PriceRec."Unit Price" := 100.0;
        PriceRec.Insert(true);

        PriceRec.Get('P2');
        Assert.AreEqual(0, Calc.CalcTotal(PriceRec, 0), 'Should be 100 * 0 = 0');
    end;
}
