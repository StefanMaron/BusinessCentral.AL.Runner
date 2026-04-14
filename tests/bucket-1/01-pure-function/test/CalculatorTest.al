codeunit 50900 "Discount Calculator Tests"
{
    Subtype = Test;

    var
        DiscountCalc: Codeunit "Discount Calculator";
        Assert: Codeunit Assert;

    [Test]
    procedure TestApplyDiscount_10Percent()
    var
        Result: Decimal;
    begin
        // [GIVEN] A price of 200 and a 10% discount
        // [WHEN] Applying the discount
        Result := DiscountCalc.ApplyDiscount(200, 10);

        // [THEN] The result should be 180
        Assert.AreEqual(180, Result, 'Expected 10% discount on 200 to be 180');
    end;

    [Test]
    procedure TestApplyDiscount_ZeroPercent()
    var
        Result: Decimal;
    begin
        Result := DiscountCalc.ApplyDiscount(100, 0);
        Assert.AreEqual(100, Result, 'Zero discount should return original price');
    end;

    [Test]
    procedure TestApplyDiscount_100Percent()
    var
        Result: Decimal;
    begin
        Result := DiscountCalc.ApplyDiscount(250, 100);
        Assert.AreEqual(0, Result, '100% discount should return zero');
    end;

    [Test]
    procedure TestCalculateVAT()
    var
        Result: Decimal;
    begin
        Result := DiscountCalc.CalculateVAT(100, 19);
        Assert.AreEqual(19, Result, 'VAT on 100 at 19% should be 19');
    end;

    [Test]
    procedure TestApplyDiscount_NegativePercent()
    begin
        // [WHEN] Applying a negative discount percentage
        asserterror DiscountCalc.ApplyDiscount(200, -10);

        // [THEN] Should error about negative percentage
        Assert.ExpectedError('Discount percentage must not be negative');
    end;

    [Test]
    procedure TestApplyDiscount_Over100Percent()
    begin
        // [WHEN] Applying a discount exceeding 100%
        asserterror DiscountCalc.ApplyDiscount(200, 150);

        // [THEN] Should error about exceeding 100
        Assert.ExpectedError('Discount percentage must not exceed 100');
    end;
}
