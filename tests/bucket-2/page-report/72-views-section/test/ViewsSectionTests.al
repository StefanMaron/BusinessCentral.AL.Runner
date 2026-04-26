codeunit 59201 "VS Views Section Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "VS Product Helper";

    // -----------------------------------------------------------------------
    // Positive: pages with views sections compile; business logic runs
    // -----------------------------------------------------------------------

    [Test]
    procedure ViewsSection_ListPageCompiles_LogicRuns()
    begin
        // [GIVEN] A compilation unit containing a list page with a views section
        // [WHEN]  Business logic in the same unit is called
        // [THEN]  It executes — views section does not block compilation
        Assert.IsTrue(Helper.CountInStock(10), 'views section: CountInStock(10) must be true');
    end;

    [Test]
    procedure ViewsSection_MultipleViews_Compile()
    begin
        // [GIVEN] A list page with three named views (AllItems, ActiveOnly, InStockOnly)
        // [WHEN]  Helper logic is called
        // [THEN]  Multiple view definitions in the same views section compile
        Assert.IsFalse(Helper.CountInStock(0), 'views section: CountInStock(0) must be false');
    end;

    [Test]
    procedure ViewsSection_FilteredViews_Compile()
    begin
        // [GIVEN] A list page with views that have Filters = where(...) clauses
        // [WHEN]  Business logic is called
        // [THEN]  Views with filter expressions do not block compilation
        Assert.AreEqual('Premium', Helper.CategoryLabel('A'), 'CategoryLabel A must be Premium');
    end;

    [Test]
    procedure ViewsSection_CategoryLabel_B()
    begin
        // Positive: category B maps to Standard
        Assert.AreEqual('Standard', Helper.CategoryLabel('B'), 'CategoryLabel B must be Standard');
    end;

    [Test]
    procedure ViewsSection_CategoryLabel_Unknown()
    begin
        // Negative: unknown category maps to Other
        Assert.AreEqual('Other', Helper.CategoryLabel('Z'), 'Unknown category must return Other');
    end;

    [Test]
    procedure ViewsSection_StockZero_IsNotInStock()
    begin
        // Negative: zero stock is not in stock
        Assert.IsFalse(Helper.CountInStock(0), 'Zero stock must return false');
    end;
}
