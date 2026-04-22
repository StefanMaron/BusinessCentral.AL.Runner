codeunit 170003 "BTT Bool To Text Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ---------------------------------------------------------------
    // Positive: Boolean FieldRef value copied to Text FieldRef, then
    // the text field is read back with (NavText)GetFieldValueSafe.
    // Before the fix this threw:
    //   "Unable to cast NavBoolean to NavText"
    // ---------------------------------------------------------------

    [Test]
    procedure CopyBoolFieldRefToTextField_True_ReturnsYes()
    var
        Helper: Codeunit "BTT Bool Helper";
        Rec: Record "BTT Bool Table";
        Result: Text[100];
    begin
        // [GIVEN] A record with Flag = true
        Rec.Id := 1;
        Rec.Flag := true;

        // [WHEN] Boolean FieldRef.Value is assigned to a Text FieldRef.Value
        //        and the text field is read back
        // Previously crashed: Unable to cast NavBoolean to NavText
        Result := Helper.CopyBoolFieldRefToTextField(Rec);

        // [THEN] A true Boolean coerced to Text yields "Yes"
        Assert.AreEqual('Yes', Result, 'CopyBoolToText for true Boolean should yield Yes');
    end;

    [Test]
    procedure CopyBoolFieldRefToTextField_False_ReturnsNo()
    var
        Helper: Codeunit "BTT Bool Helper";
        Rec: Record "BTT Bool Table";
        Result: Text[100];
    begin
        // [GIVEN] A record with Flag = false
        Rec.Id := 2;
        Rec.Flag := false;

        // [WHEN] Boolean FieldRef.Value is assigned to a Text FieldRef.Value
        Result := Helper.CopyBoolFieldRefToTextField(Rec);

        // [THEN] A false Boolean coerced to Text yields "No"
        Assert.AreEqual('No', Result, 'CopyBoolToText for false Boolean should yield No');
    end;

    // ---------------------------------------------------------------
    // Negative: must not return empty (proves coercion is real)
    // ---------------------------------------------------------------

    [Test]
    procedure CopyBoolFieldRefToTextField_True_IsNotEmpty()
    var
        Helper: Codeunit "BTT Bool Helper";
        Rec: Record "BTT Bool Table";
        Result: Text[100];
    begin
        // [GIVEN] A record with Flag = true
        Rec.Id := 3;
        Rec.Flag := true;

        // [WHEN] Boolean → Text FieldRef copy
        Result := Helper.CopyBoolFieldRefToTextField(Rec);

        // [THEN] Must not be empty — a no-op stub returning '' would fail this
        Assert.AreNotEqual('', Result, 'Coerced Boolean text field must not be empty');
    end;

    // ---------------------------------------------------------------
    // Regression guard: direct field Format still works
    // ---------------------------------------------------------------

    [Test]
    procedure BoolFieldDirect_True_FormatsAsYes()
    var
        Helper: Codeunit "BTT Bool Helper";
        Rec: Record "BTT Bool Table";
    begin
        Rec.Id := 4;
        Rec.Flag := true;
        Assert.AreEqual('Yes', Helper.GetFlagAsText(Rec), 'Format(Rec.Flag) for true should yield Yes');
    end;

    [Test]
    procedure BoolFieldDirect_False_FormatsAsNo()
    var
        Helper: Codeunit "BTT Bool Helper";
        Rec: Record "BTT Bool Table";
    begin
        Rec.Id := 5;
        Rec.Flag := false;
        Assert.AreEqual('No', Helper.GetFlagAsText(Rec), 'Format(Rec.Flag) for false should yield No');
    end;
}
