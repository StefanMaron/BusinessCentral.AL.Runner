codeunit 59501 "GS Grid Section Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "GS Product Helper";

    // -----------------------------------------------------------------------
    // Positive: pages with grid sections compile; business logic runs
    // -----------------------------------------------------------------------

    [Test]
    procedure GridSection_CardPageCompiles_LogicRuns()
    begin
        // [GIVEN] A compilation unit containing a Card page with a grid section
        // [WHEN]  Business logic in the same unit is called
        // [THEN]  It executes — grid section does not block compilation
        Assert.AreEqual(250, Helper.CalcTotal(50, 5), 'grid section: 50 * 5 must be 250');
    end;

    [Test]
    procedure GridSection_MultipleGrids_Compile()
    begin
        // [GIVEN] A Card page with two grid sections (TopGrid and PricingGrid)
        // [WHEN]  Business logic is called
        // [THEN]  Multiple grid declarations in the same page compile
        Assert.AreEqual(0, Helper.CalcTotal(100, 0), 'grid section: 100 * 0 must be 0');
    end;

    [Test]
    procedure GridSection_IsExpensive_True()
    begin
        // Positive: price above threshold is expensive
        Assert.IsTrue(Helper.IsExpensive(150), 'Price 150 must be expensive');
    end;

    [Test]
    procedure GridSection_IsExpensive_False()
    begin
        // Negative: price at/below threshold is not expensive
        Assert.IsFalse(Helper.IsExpensive(100), 'Price exactly 100 must not be expensive');
    end;

    [Test]
    procedure GridSection_FormatLabel_InStock()
    begin
        // Positive: in-stock item includes quantity in label
        Assert.AreEqual('Widget (10)', Helper.FormatLabel('Widget', 10), 'In-stock label must include quantity');
    end;

    [Test]
    procedure GridSection_FormatLabel_OutOfStock()
    begin
        // Negative: out-of-stock item shows out-of-stock label
        Assert.AreEqual('Widget (out of stock)', Helper.FormatLabel('Widget', 0), 'Zero quantity must show out-of-stock');
    end;
}
