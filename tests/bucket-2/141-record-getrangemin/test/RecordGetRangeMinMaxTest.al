codeunit 60001 "GRM GetRangeMinMax Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure GetRangeMin_Integer_ReturnsLowerBound()
    var
        Helper: Codeunit "GRM Helper";
    begin
        // Positive: GetRangeMin returns the lower bound of a SetRange(Quantity, 5, 10) call.
        Assert.AreEqual(5, Helper.GetMinQty(5, 10), 'GetRangeMin must return the lower bound 5');
        Assert.AreEqual(0, Helper.GetMinQty(0, 100), 'GetRangeMin must return 0 for range 0..100');
        Assert.AreEqual(-20, Helper.GetMinQty(-20, -5), 'GetRangeMin must handle negative lower bounds');
    end;

    [Test]
    procedure GetRangeMax_Integer_ReturnsUpperBound()
    var
        Helper: Codeunit "GRM Helper";
    begin
        // Positive: GetRangeMax returns the upper bound of a SetRange(Quantity, 5, 10) call.
        Assert.AreEqual(10, Helper.GetMaxQty(5, 10), 'GetRangeMax must return the upper bound 10');
        Assert.AreEqual(100, Helper.GetMaxQty(0, 100), 'GetRangeMax must return 100 for range 0..100');
        Assert.AreEqual(-5, Helper.GetMaxQty(-20, -5), 'GetRangeMax must handle negative upper bounds');
    end;

    [Test]
    procedure GetRangeMin_NotMax()
    var
        Helper: Codeunit "GRM Helper";
    begin
        // Negative: GetRangeMin must NOT return the upper bound.
        Assert.AreNotEqual(10, Helper.GetMinQty(5, 10), 'GetRangeMin must not return the upper bound');
    end;

    [Test]
    procedure GetRangeMax_NotMin()
    var
        Helper: Codeunit "GRM Helper";
    begin
        // Negative: GetRangeMax must NOT return the lower bound.
        Assert.AreNotEqual(5, Helper.GetMaxQty(5, 10), 'GetRangeMax must not return the lower bound');
    end;

    [Test]
    procedure GetRangeMin_Decimal_ReturnsLowerBound()
    var
        Helper: Codeunit "GRM Helper";
    begin
        // Positive: GetRangeMin works with Decimal fields.
        Assert.AreEqual(1.5, Helper.GetMinPrice(1.5, 9.99), 'GetRangeMin(Decimal) must return lower bound 1.5');
        Assert.AreEqual(0.0, Helper.GetMinPrice(0.0, 100.0), 'GetRangeMin(Decimal) must return 0.0');
    end;

    [Test]
    procedure GetRangeMax_Decimal_ReturnsUpperBound()
    var
        Helper: Codeunit "GRM Helper";
    begin
        // Positive: GetRangeMax works with Decimal fields.
        Assert.AreEqual(9.99, Helper.GetMaxPrice(1.5, 9.99), 'GetRangeMax(Decimal) must return upper bound 9.99');
        Assert.AreEqual(100.0, Helper.GetMaxPrice(0.0, 100.0), 'GetRangeMax(Decimal) must return 100.0');
    end;

    [Test]
    procedure GetRangeMin_EqualityRange_ReturnsSameValue()
    var
        Helper: Codeunit "GRM Helper";
    begin
        // Edge case: SetRange(Field, x) is a single-value range — both min and max equal x.
        Assert.AreEqual(7, Helper.GetMinQty(7, 7), 'GetRangeMin of single-value range must return that value');
    end;

    [Test]
    procedure GetRangeMax_EqualityRange_ReturnsSameValue()
    var
        Helper: Codeunit "GRM Helper";
    begin
        // Edge case: GetRangeMax of a single-value range must also return that value.
        Assert.AreEqual(7, Helper.GetMaxQty(7, 7), 'GetRangeMax of single-value range must return that value');
    end;

    [Test]
    procedure AddWithBonus_ProvingCompilationUnitLive()
    var
        Helper: Codeunit "GRM Helper";
    begin
        // Proving: helper performs real work, proving the compilation unit is live.
        Assert.AreEqual(8, Helper.AddWithBonus(3, 4), 'AddWithBonus(3,4) must return 3+4+1=8');
        Assert.AreEqual(1, Helper.AddWithBonus(0, 0), 'AddWithBonus(0,0) must return 0+0+1=1');
    end;

    [Test]
    procedure AddWithBonus_NotPlainSum()
    var
        Helper: Codeunit "GRM Helper";
    begin
        // Negative: AddWithBonus must NOT return a plain sum (no-op trap guard).
        Assert.AreNotEqual(7, Helper.AddWithBonus(3, 4), 'AddWithBonus must not just return a+b');
    end;
}
