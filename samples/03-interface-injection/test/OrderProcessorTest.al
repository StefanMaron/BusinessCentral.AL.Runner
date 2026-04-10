codeunit 50902 "Order Processor Tests"
{
    Subtype = Test;

    var
        OrderProc: Codeunit "Order Processor";
        Assert: Codeunit "Library Assert";

    [Test]
    procedure TestProcessOrder_SimpleOrder()
    var
        MockPricing: Codeunit "Mock Pricing Service";
        Total: Decimal;
    begin
        // [GIVEN] A pricing service returning $25 per unit, item available
        MockPricing.SetMockPrice(25.00);
        MockPricing.SetMockAvailable(true);

        // [WHEN] Processing an order for 3 items
        Total := OrderProc.ProcessOrder('WIDGET-01', 3, MockPricing);

        // [THEN] Total should be 75
        Assert.AreEqual(75, Total, 'Total for 3 items at $25 should be $75');
    end;

    [Test]
    procedure TestProcessOrder_BulkDiscount()
    var
        MockPricing: Codeunit "Mock Pricing Service";
        Total: Decimal;
    begin
        // [GIVEN] 10+ items triggers 10% bulk discount
        MockPricing.SetMockPrice(10.00);
        MockPricing.SetMockAvailable(true);

        // [WHEN] Processing an order for 10 items
        Total := OrderProc.ProcessOrder('WIDGET-01', 10, MockPricing);

        // [THEN] Total should be 10*10*0.9 = 90
        Assert.AreEqual(90, Total, 'Bulk order of 10 at $10 with 10% discount should be $90');
    end;

    [Test]
    procedure TestProcessOrder_ItemUnavailable()
    var
        MockPricing: Codeunit "Mock Pricing Service";
    begin
        // [GIVEN] Item is not available
        MockPricing.SetMockPrice(50.00);
        MockPricing.SetMockAvailable(false);

        // [WHEN/THEN] Processing order should throw error
        asserterror OrderProc.ProcessOrder('GONE-01', 1, MockPricing);
        Assert.ExpectedError('Item GONE-01 is not available');
    end;
}
