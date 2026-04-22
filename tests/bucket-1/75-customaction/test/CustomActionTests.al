codeunit 59601 "CA Custom Action Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "CA Order Helper";
        PageRunner: Codeunit "CA Page Runner";

    // -----------------------------------------------------------------------
    // Positive: pages with customaction declarations compile; logic runs
    // -----------------------------------------------------------------------

    [Test]
    procedure CustomAction_FlowType_Compiles_LogicRuns()
    begin
        // [GIVEN] A page with a customaction(FlowType) declaration
        // [WHEN]  Business logic in the same compilation unit is called
        // [THEN]  It executes — customaction declaration does not block compilation
        Assert.AreEqual(10, Helper.CalcTax(100, 10), 'customaction: 10% tax on 100 must be 10');
    end;

    [Test]
    procedure CustomAction_FlowTemplate_Compiles()
    begin
        // [GIVEN] A page with a customaction(FlowTemplate) declaration
        // [WHEN]  Business logic is called
        // [THEN]  FlowTemplate customaction does not block compilation
        Assert.AreEqual(0, Helper.CalcTax(0, 20), 'customaction: 20% tax on 0 must be 0');
    end;

    [Test]
    procedure CustomAction_FlowTemplateGallery_PageInstantiates()
    begin
        // [GIVEN] A page with customaction(FlowTemplateGallery) in the action area
        // [WHEN]  The page is instantiated and run
        // [THEN]  Compilation succeeds and Run() is a no-op — FlowTemplateGallery
        //         is out of scope at runtime (no Power Automate integration) but
        //         must not prevent the page from compiling or being used.
        PageRunner.RunOrderCard();
        Assert.IsTrue(true, 'Page with FlowTemplateGallery customaction must compile and run as no-op');
    end;

    [Test]
    procedure CustomAction_MultipleCustomActions_Compile()
    begin
        // [GIVEN] A page with multiple customaction declarations (Flow + FlowTemplateGallery)
        // [WHEN]  Business logic is called
        // [THEN]  Multiple customactions in different areas compile together
        Assert.AreEqual(25, Helper.CalcTax(500, 5), 'customaction: 5% tax on 500 must be 25');
    end;

    [Test]
    procedure CustomAction_IsLargeOrder_True()
    begin
        // Positive: amount at threshold is large
        Assert.IsTrue(Helper.IsLargeOrder(1000), 'Amount 1000 must be a large order');
    end;

    [Test]
    procedure CustomAction_IsLargeOrder_False()
    begin
        // Negative: amount below threshold is not large
        Assert.IsFalse(Helper.IsLargeOrder(999), 'Amount 999 must not be a large order');
    end;

    [Test]
    procedure CustomAction_FormatOrderRef()
    begin
        // Positive: format includes order number and amount
        Assert.AreEqual('ORD-001: 250', Helper.FormatOrderRef('ORD-001', 250), 'FormatOrderRef must include order number and amount');
    end;
}
