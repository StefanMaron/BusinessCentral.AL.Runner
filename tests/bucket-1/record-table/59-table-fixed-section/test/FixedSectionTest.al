codeunit 59401 "Test Table Fixed Section"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure FixedSection_InsertAndGet()
    var
        Rec: Record "Fixed Section Test Table";
    begin
        // [GIVEN] A table with fieldgroup(Fixed; ...) compiles and allows Insert
        Rec.Id := 1;
        Rec.Name := 'Widget';
        Rec.Amount := 99.50;
        Rec.Insert();

        // [WHEN] Get the record by PK
        Rec.Get(1);

        // [THEN] All fields retain their values
        Assert.AreEqual(1, Rec.Id, 'Id must be 1');
        Assert.AreEqual('Widget', Rec.Name, 'Name must be Widget');
        Assert.AreEqual(99.50, Rec.Amount, 'Amount must be 99.50');
    end;

    [Test]
    procedure FixedSection_Modify()
    var
        Rec: Record "Fixed Section Test Table";
    begin
        // [GIVEN] An inserted record on a table with fieldgroup(Fixed; ...)
        Rec.Id := 2;
        Rec.Name := 'Original';
        Rec.Amount := 10.00;
        Rec.Insert();

        // [WHEN] Modify the record
        Rec.Get(2);
        Rec.Name := 'Modified';
        Rec.Modify();

        // [THEN] The modified field is persisted
        Rec.Get(2);
        Assert.AreEqual('Modified', Rec.Name, 'Name must reflect modification');
    end;

    [Test]
    procedure FixedSection_Delete()
    var
        Rec: Record "Fixed Section Test Table";
    begin
        // [GIVEN] An inserted record on a table with fieldgroup(Fixed; ...)
        Rec.Id := 3;
        Rec.Name := 'ToDelete';
        Rec.Amount := 5.00;
        Rec.Insert();

        // [WHEN] Delete the record
        Rec.Get(3);
        Rec.Delete();

        // [THEN] Get returns false
        Assert.IsFalse(Rec.Get(3), 'Get must return false after Delete');
    end;

    [Test]
    procedure FixedSection_GetNonExistent_ReturnsFalse()
    var
        Rec: Record "Fixed Section Test Table";
    begin
        // [GIVEN] No record with Id 999 exists
        // [WHEN] Get is called for a non-existent key
        // [THEN] Get returns false — fieldgroup(Fixed) does not affect key lookups
        Assert.IsFalse(Rec.Get(999), 'Get must return false for missing key');
    end;

    [Test]
    procedure FixedSection_DuplicateKey_Errors()
    var
        Rec: Record "Fixed Section Test Table";
    begin
        // [GIVEN] A record with Id 4 already exists
        Rec.Id := 4;
        Rec.Name := 'First';
        Rec.Amount := 1.00;
        Rec.Insert();

        // [WHEN/THEN] Inserting a second record with the same key errors
        Rec.Id := 4;
        Rec.Name := 'Duplicate';
        asserterror Rec.Insert();
        Assert.ExpectedError('already exists');
    end;

    // --- Page layout fixed() group tests ---

    [Test]
    procedure FixedLayoutGroup_SetAndReadLeft()
    var
        TP: TestPage "Fixed Section Page";
    begin
        // [GIVEN] A page with a fixed() layout group containing grpLeft and grpRight
        TP.OpenNew();

        // [WHEN] Fields in the left group are set
        TP.IdField.SetValue(10);
        TP.NameField.SetValue('Alpha');

        // [THEN] Values are readable — fixed() groups do not break field access
        Assert.AreEqual('10', TP.IdField.Value, 'IdField in fixed() group must be 10');
        Assert.AreEqual('Alpha', TP.NameField.Value, 'NameField in fixed() group must be Alpha');
        TP.Close();
    end;

    [Test]
    procedure FixedLayoutGroup_SetAndReadRight()
    var
        TP: TestPage "Fixed Section Page";
    begin
        // [GIVEN] A page with a fixed() layout group
        TP.OpenNew();

        // [WHEN] A field in the right group is set
        TP.AmountField.SetValue(999.99);

        // [THEN] The value is readable from the right-side fixed() group
        Assert.AreEqual('999.99', TP.AmountField.Value, 'AmountField in fixed() right group must be 999.99');
        TP.Close();
    end;

    [Test]
    procedure FixedLayoutGroup_FieldValue_NotDefaultAfterSet()
    var
        TP: TestPage "Fixed Section Page";
    begin
        // [GIVEN] A page with a fixed() layout group, name field set to a specific value
        TP.OpenNew();
        TP.NameField.SetValue('Beta');

        // [THEN] The value is not the default empty string
        Assert.AreNotEqual('', TP.NameField.Value, 'NameField must not be empty after SetValue');
        Assert.AreEqual('Beta', TP.NameField.Value, 'NameField must be Beta');
        TP.Close();
    end;
}
