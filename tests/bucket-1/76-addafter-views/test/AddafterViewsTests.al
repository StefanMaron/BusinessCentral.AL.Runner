codeunit 59701 "AV Addafter Views Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "AV Customer Helper";

    // -----------------------------------------------------------------------
    // Positive: pageextension with addafter(views) compiles; business logic runs
    // -----------------------------------------------------------------------

    [Test]
    procedure AddafterViews_PageExtCompiles_LogicRuns()
    begin
        // [GIVEN] A pageextension using addafter() in the views area
        // [WHEN]  Business logic in the same compilation unit is called
        // [THEN]  It executes — addafter(views) declaration does not block compilation
        Assert.IsTrue(Helper.IsHighBalance(1001), 'addafter views: balance 1001 must be high');
    end;

    [Test]
    procedure AddafterViews_MultipleViews_Compile()
    begin
        // [GIVEN] A pageextension adding two views via addafter()
        // [WHEN]  Business logic is called
        // [THEN]  Multiple addafter view declarations compile together
        Assert.IsFalse(Helper.IsHighBalance(1000), 'addafter views: balance exactly 1000 must not be high');
    end;

    [Test]
    procedure AddafterViews_IsHighBalance_True()
    begin
        // Positive: balance above threshold
        Assert.IsTrue(Helper.IsHighBalance(5000), 'Balance 5000 must be high');
    end;

    [Test]
    procedure AddafterViews_IsHighBalance_False()
    begin
        // Negative: balance at threshold is not high
        Assert.IsFalse(Helper.IsHighBalance(1000), 'Balance exactly 1000 must not be high');
    end;

    [Test]
    procedure AddafterViews_FormatStatus_Active()
    begin
        // Positive: active record formats as Active
        Assert.AreEqual('Active', Helper.FormatStatus(true), 'Active must format as Active');
    end;

    [Test]
    procedure AddafterViews_FormatStatus_Inactive()
    begin
        // Negative: inactive record formats as Inactive
        Assert.AreEqual('Inactive', Helper.FormatStatus(false), 'Inactive must format as Inactive');
    end;

    [Test]
    procedure AddafterViews_CalcCategory_Premium()
    begin
        // Positive: high balance is Premium
        Assert.AreEqual('Premium', Helper.CalcCategory(5001), 'Balance 5001 must be Premium');
    end;

    [Test]
    procedure AddafterViews_CalcCategory_Standard()
    begin
        // Positive: mid balance is Standard
        Assert.AreEqual('Standard', Helper.CalcCategory(2000), 'Balance 2000 must be Standard');
    end;

    [Test]
    procedure AddafterViews_CalcCategory_Basic()
    begin
        // Negative: low balance is Basic
        Assert.AreEqual('Basic', Helper.CalcCategory(500), 'Balance 500 must be Basic');
    end;
}
