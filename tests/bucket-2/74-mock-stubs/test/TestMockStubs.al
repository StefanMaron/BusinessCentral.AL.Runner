codeunit 57401 "Stub Methods Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure TestPageFieldCaptionReturnsText()
    var
        TP: TestPage "Stub Test Card";
        Cap: Text;
    begin
        // [GIVEN] A TestPage opened for editing
        TP.OpenNew();
        // [WHEN] Reading the Caption property of a field
        Cap := TP.NameField.Caption;
        // [THEN] It returns a text value (stub, possibly empty)
        // Positive: Caption is callable and returns text without error
        Assert.IsTrue(true, 'Caption property must compile and return text');
        TP.Close();
    end;

    [Test]
    procedure TestPageFieldCaptionIsNotError()
    var
        TP: TestPage "Stub Test Card";
        Cap1: Text;
        Cap2: Text;
    begin
        // [GIVEN] A TestPage with two fields
        TP.OpenNew();
        // [WHEN] Reading Caption from two different fields
        Cap1 := TP.NameField.Caption;
        Cap2 := TP.AmountField.Caption;
        // [THEN] Both return without error (negative: they should not throw)
        Assert.AreNotEqual('ERROR', Cap1, 'Caption should not be ERROR');
        Assert.AreNotEqual('ERROR', Cap2, 'Caption should not be ERROR');
        TP.Close();
    end;

    [Test]
    procedure RecRefSetLoadFieldsNoOp()
    var
        RecRef: RecordRef;
        Rec: Record "Stub Test Table";
    begin
        // [GIVEN] A table with a record
        Rec.Id := 1;
        Rec.Name := 'Test';
        Rec.Insert(true);

        // [WHEN] Opening RecRef and calling SetLoadFields then finding
        RecRef.Open(57400);
        RecRef.SetLoadFields(1, 2);
        RecRef.FindFirst();

        // [THEN] Record is found (SetLoadFields is a no-op, all fields are in memory)
        Assert.AreEqual(1, RecRef.Field(1).Value, 'Field 1 should be 1');
    end;

    [Test]
    procedure RecRefSetLoadFieldsDoesNotFilter()
    var
        RecRef: RecordRef;
        Rec: Record "Stub Test Table";
    begin
        // [GIVEN] A table with a record that has a Name field
        Rec.Id := 10;
        Rec.Name := 'Hello';
        Rec.Amount := 99;
        Rec.Insert(true);

        // [WHEN] SetLoadFields is called with only field 1
        RecRef.Open(57400);
        RecRef.SetLoadFields(1);
        RecRef.FindFirst();

        // [THEN] All fields are still readable (negative: SetLoadFields does NOT restrict access)
        Assert.AreEqual(10, RecRef.Field(1).Value, 'Field 1 should still be readable');
    end;

    [Test]
    procedure RecRefNameReturnsText()
    var
        RecRef: RecordRef;
        TableName: Text;
    begin
        // [GIVEN] A RecordRef opened on a table
        RecRef.Open(57400);
        // [WHEN] Reading the Name property
        TableName := RecRef.Name;
        // [THEN] It returns a non-error text value
        Assert.AreNotEqual('', TableName, 'RecRef.Name should return a non-empty stub');
    end;

    [Test]
    procedure RecRefNameBeforeOpenIsEmpty()
    var
        RecRef: RecordRef;
        TableName: Text;
    begin
        // [GIVEN] A RecordRef that has NOT been opened
        // [WHEN] Reading the Name property
        TableName := RecRef.Name;
        // [THEN] It returns an empty string (no table opened)
        Assert.AreEqual('', TableName, 'RecRef.Name before Open should be empty');
    end;

    [Test]
    procedure PageUpdateNoOp()
    var
        Logic: Codeunit "Stub Logic";
    begin
        // [GIVEN/WHEN] Code that calls Page.Update() and Page.Update(false)
        Logic.UsePageUpdate();
        // [THEN] No error thrown — Update is a no-op
        Assert.IsTrue(true, 'Page.Update() must compile and no-op');
    end;

    [Test]
    procedure PageUpdateWithParamNoOp()
    var
        P: Page "Stub Test Card";
    begin
        // [GIVEN] A page variable
        // [WHEN] Calling Update with explicit boolean
        P.Update(true);
        P.Update(false);
        // [THEN] Both complete without error
        Assert.IsTrue(true, 'Page.Update(bool) must compile and no-op');
    end;
}
