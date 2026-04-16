codeunit 58801 "PEM PageExt Modification Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "PEM Product Helper";

    // -----------------------------------------------------------------------
    // Positive: codeunit in the same compilation unit as pageextensions
    // using addafter/addbefore compiles and executes correctly.
    // -----------------------------------------------------------------------

    [Test]
    procedure Addafter_PageExtCompiles_BusinessLogicRuns()
    begin
        // [GIVEN] A compilation unit containing a pageextension with addafter(...)
        // [WHEN]  Business logic in the same unit is called
        // [THEN]  It executes correctly — addafter modification does not block compilation
        Assert.AreEqual(90, Helper.CalcDiscountedPrice(100, 10), 'addafter: 10% off 100 should be 90');
    end;

    [Test]
    procedure Addbefore_PageExtCompiles_BusinessLogicRuns()
    begin
        // [GIVEN] A compilation unit containing a pageextension with addbefore(...)
        // [WHEN]  Business logic in the same unit is called
        // [THEN]  It executes correctly — addbefore modification does not block compilation
        Assert.AreEqual(75, Helper.CalcDiscountedPrice(100, 25), 'addbefore: 25% off 100 should be 75');
    end;

    [Test]
    procedure Addafter_FullPriceNoDiscount()
    begin
        // Positive: zero discount returns original price
        Assert.AreEqual(200, Helper.CalcDiscountedPrice(200, 0), 'Zero discount should return original price');
    end;

    [Test]
    procedure Addbefore_IsExpensive_AboveThreshold()
    begin
        // Positive: price above 1000 is expensive
        Assert.IsTrue(Helper.IsExpensive(1500), 'Price 1500 should be expensive');
    end;

    [Test]
    procedure Addafter_IsExpensive_BelowThreshold()
    begin
        // Negative: price at or below 1000 is not expensive
        Assert.IsFalse(Helper.IsExpensive(999), 'Price 999 should not be expensive');
    end;

    [Test]
    procedure Addafter_Addbefore_BothExtensionsCoexist()
    begin
        // [GIVEN] Two pageextensions in the same unit — one with addafter, one with addbefore
        // [WHEN]  Business logic is executed
        // [THEN]  Both extensions compile together without conflict
        Assert.AreEqual(0, Helper.CalcDiscountedPrice(0, 50), 'Zero price stays zero regardless of discount');
    end;
}
