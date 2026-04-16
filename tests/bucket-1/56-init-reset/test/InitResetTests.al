codeunit 56401 "Init Reset Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // -----------------------------------------------------------------------
    // Init clears non-PK fields to type defaults
    // -----------------------------------------------------------------------

    [Test]
    procedure Init_ClearsTextFieldToDefault()
    var
        Rec: Record "IR Test";
    begin
        // [GIVEN] Name field set to a non-empty value
        Rec.Name := 'Something';
        Assert.AreEqual('Something', Rec.Name, 'Precondition: Name should be set');

        // [WHEN] Init() is called
        Rec.Init();

        // [THEN] Name is reset to empty string (type default)
        Assert.AreEqual('', Rec.Name, 'Init() must reset Text field to empty string');
    end;

    [Test]
    procedure Init_ClearsDecimalFieldToZero()
    var
        Rec: Record "IR Test";
    begin
        // [GIVEN] Amount set to a non-zero value
        Rec.Amount := 99.5;

        // [WHEN] Init() is called
        Rec.Init();

        // [THEN] Amount is reset to 0
        Assert.AreEqual(0, Rec.Amount, 'Init() must reset Decimal field to 0');
    end;

    [Test]
    procedure Init_ClearsIntegerFieldToZero()
    var
        Rec: Record "IR Test";
    begin
        // [GIVEN] Qty set to non-zero
        Rec.Qty := 42;

        // [WHEN] Init() is called
        Rec.Init();

        // [THEN] Qty is reset to 0
        Assert.AreEqual(0, Rec.Qty, 'Init() must reset Integer field to 0');
    end;

    [Test]
    procedure Init_ClearsBooleanFieldToFalse()
    var
        Rec: Record "IR Test";
    begin
        // [GIVEN] Active set to true
        Rec.Active := true;

        // [WHEN] Init() is called
        Rec.Init();

        // [THEN] Active is reset to false (Boolean default)
        Assert.AreEqual(false, Rec.Active, 'Init() must reset Boolean field to false');
    end;

    // -----------------------------------------------------------------------
    // Init preserves PK fields
    // -----------------------------------------------------------------------

    [Test]
    procedure Init_PreservesPKField()
    var
        Rec: Record "IR Test";
    begin
        // [GIVEN] PK set and non-PK fields modified
        Rec."No." := 'X001';
        Rec.Name := 'Dirty';
        Rec.Amount := 100;

        // [WHEN] Init() is called
        Rec.Init();

        // [THEN] PK is preserved, non-PK fields cleared
        Assert.AreEqual('X001', Rec."No.", 'Init() must preserve PK field value');
        Assert.AreEqual('', Rec.Name, 'Init() must clear non-PK Name field');
        Assert.AreEqual(0, Rec.Amount, 'Init() must clear non-PK Amount field');
    end;

    // -----------------------------------------------------------------------
    // Init applies InitValue property
    // -----------------------------------------------------------------------

    [Test]
    procedure Init_AppliesInitValue()
    var
        Rec: Record "IR Test";
    begin
        // [GIVEN] Score field has InitValue = 5; first set it to something else
        Rec.Score := 99;

        // [WHEN] Init() is called
        Rec.Init();

        // [THEN] Score is reset to InitValue = 5, not type default 0
        Assert.AreEqual(5, Rec.Score, 'Init() must set field with InitValue = 5 to 5');
    end;

    // -----------------------------------------------------------------------
    // Init after Insert+Modify resets in-memory values
    // -----------------------------------------------------------------------

    [Test]
    procedure Init_AfterInsertAndModify_ResetsInMemory()
    var
        Rec: Record "IR Test";
    begin
        // [GIVEN] A record is inserted and retrieved
        Rec.DeleteAll();
        Rec.Init();
        Rec."No." := 'TEST';
        Rec.Name := 'Original';
        Rec.Amount := 50;
        Rec.Insert();

        Rec.Get('TEST');
        Rec.Name := 'Modified';
        Rec.Amount := 200;
        Rec.Modify();

        // [WHEN] Init() is called on the in-memory variable
        Rec.Init();

        // [THEN] In-memory Name and Amount are reset; PK is preserved
        Assert.AreEqual('TEST', Rec."No.", 'Init() must preserve PK after Modify');
        Assert.AreEqual('', Rec.Name, 'Init() must reset Name to default after Modify');
        Assert.AreEqual(0, Rec.Amount, 'Init() must reset Amount to 0 after Modify');

        // [AND] The stored record is unchanged (Init only affects in-memory)
        Rec.Get('TEST');
        Assert.AreEqual('Modified', Rec.Name, 'Stored record Name must not be affected by Init()');
        Assert.AreEqual(200, Rec.Amount, 'Stored record Amount must not be affected by Init()');
    end;
}
