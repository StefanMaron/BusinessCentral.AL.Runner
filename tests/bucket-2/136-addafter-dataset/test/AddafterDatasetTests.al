codeunit 60101 "AADS Addafter Dataset Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "AADS Helper";

    // -----------------------------------------------------------------------
    // Positive: reportextension with addafter(dataset) compiles; logic runs
    // -----------------------------------------------------------------------

    [Test]
    procedure AddafterDataset_ReportExtCompiles_LogicRuns()
    begin
        // [GIVEN] A reportextension using addafter() in the dataset area
        // [WHEN]  Business logic in the same compilation unit is called
        // [THEN]  It executes — addafter(dataset) declaration does not block compilation
        Assert.AreEqual('After Dataset Helper', Helper.GetLabel(), 'Helper must return expected label');
    end;

    [Test]
    procedure AddafterDataset_WrongValue_Fails()
    begin
        // Negative: asserterror proves the assertion fires for wrong values
        asserterror Assert.AreEqual('Wrong', Helper.GetLabel(), '');
        Assert.ExpectedError('Wrong');
    end;

    [Test]
    procedure AddafterDataset_IsLargeQuantity_True()
    begin
        // Positive: quantity at threshold is large
        Assert.IsTrue(Helper.IsLargeQuantity(100), 'Quantity 100 must be large');
    end;

    [Test]
    procedure AddafterDataset_IsLargeQuantity_False()
    begin
        // Negative: quantity below threshold is not large
        Assert.IsFalse(Helper.IsLargeQuantity(99), 'Quantity 99 must not be large');
    end;

    [Test]
    procedure AddafterDataset_FormatItem_IncludesNo()
    begin
        // Positive: formatted item includes the item number
        Assert.AreEqual('ITM001: Widget', Helper.FormatItem('ITM001', 'Widget'), 'Format must include No and Description');
    end;

    [Test]
    procedure AddafterDataset_FormatItem_EmptyDesc()
    begin
        // Edge case: empty description still formats correctly
        Assert.AreEqual('ITM002: ', Helper.FormatItem('ITM002', ''), 'Format with empty description must have trailing colon-space');
    end;
}
