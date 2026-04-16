codeunit 60401 "ALV Addlast Views Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "ALV Helper";

    // -----------------------------------------------------------------------
    // Positive: pageextension with addlast(views) compiles; logic runs
    // -----------------------------------------------------------------------

    [Test]
    procedure AddlastViews_PageExtCompiles_LogicRuns()
    begin
        // [GIVEN] A pageextension using addlast() in the views area
        // [WHEN]  Business logic in the same compilation unit is called
        // [THEN]  It executes — addlast(views) declaration does not block compilation
        Assert.AreEqual('addlast views ok', Helper.GetLabel(), 'Helper must return expected label');
    end;

    [Test]
    procedure AddlastViews_WrongValue_Fails()
    begin
        // Negative: asserterror proves the assertion fires for wrong values
        asserterror Assert.AreEqual('Wrong', Helper.GetLabel(), '');
        Assert.ExpectedError('Wrong');
    end;

    [Test]
    procedure AddlastViews_IsActive_True()
    begin
        // Positive: active record formats as Active
        Assert.AreEqual('Active', Helper.IsActive(true), 'Active must format as Active');
    end;

    [Test]
    procedure AddlastViews_IsActive_False()
    begin
        // Negative: inactive record formats as Inactive
        Assert.AreEqual('Inactive', Helper.IsActive(false), 'Inactive must format as Inactive');
    end;

    [Test]
    procedure AddlastViews_FormatCode_NonEmpty()
    begin
        // Positive: code and name combined correctly
        Assert.AreEqual('PRD001: Widget', Helper.FormatCode('PRD001', 'Widget'), 'FormatCode must combine Code and Name');
    end;

    [Test]
    procedure AddlastViews_FormatCode_EmptyName()
    begin
        // Edge case: empty name produces trailing colon-space
        Assert.AreEqual('PRD002: ', Helper.FormatCode('PRD002', ''), 'FormatCode with empty name must have trailing colon-space');
    end;
}
