codeunit 60301 "ALFG Addlast Fieldgroup Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "ALFG Helper";

    // -----------------------------------------------------------------------
    // Positive: tableextension with addlast(fieldgroups) compiles; logic runs
    // -----------------------------------------------------------------------

    [Test]
    procedure AddlastFieldgroup_TableExtCompiles_LogicRuns()
    begin
        // [GIVEN] A tableextension using addlast() in the fieldgroups section
        // [WHEN]  Business logic in the same compilation unit is called
        // [THEN]  It executes — addlast(fieldgroups) declaration does not block compilation
        Assert.AreEqual('addlast fieldgroup ok', Helper.GetLabel(), 'Helper must return expected label');
    end;

    [Test]
    procedure AddlastFieldgroup_WrongValue_Fails()
    begin
        // Negative: asserterror proves the assertion fires for wrong values
        asserterror Assert.AreEqual('Wrong', Helper.GetLabel(), '');
        Assert.ExpectedError('Wrong');
    end;

    [Test]
    procedure AddlastFieldgroup_IsLongName_True()
    begin
        // Positive: name longer than 20 chars is long
        Assert.IsTrue(Helper.IsLongName('This Is A Long Name XY'), 'Name > 20 chars must be long');
    end;

    [Test]
    procedure AddlastFieldgroup_IsLongName_False()
    begin
        // Negative: name exactly 20 chars is not long
        Assert.IsFalse(Helper.IsLongName('Exactly Twenty Chars'), 'Name of exactly 20 chars must not be long');
    end;

    [Test]
    procedure AddlastFieldgroup_FormatItem_IncludesNo()
    begin
        // Positive: formatted item includes No and Name
        Assert.AreEqual('ITM001 - Widget', Helper.FormatItem('ITM001', 'Widget'), 'Format must include No and Name');
    end;

    [Test]
    procedure AddlastFieldgroup_FormatItem_EmptyName()
    begin
        // Edge case: empty name still formats correctly
        Assert.AreEqual('ITM002 - ', Helper.FormatItem('ITM002', ''), 'Format with empty name must have trailing dash-space');
    end;
}
