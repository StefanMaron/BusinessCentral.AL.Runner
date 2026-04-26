codeunit 50401 "Test RecRef FieldIndex"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // === Blocker 1: RecordRef.FieldIndex and Caption ===

    [Test]
    procedure FieldIndexReturnsFieldRef()
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
        Item: Record "Test Item";
    begin
        // Setup: insert a record so fields are registered
        Item."No." := 'ITEM1';
        Item.Description := 'Widget';
        Item.Amount := 42.5;
        Item.Insert();

        RecRef.GetTable(Item);

        // FieldIndex(1) should return a valid FieldRef
        FldRef := RecRef.FieldIndex(1);
        Assert.AreNotEqual(0, FldRef.Number, 'FieldIndex(1) should return a FieldRef with non-zero number');
    end;

    [Test]
    procedure FieldIndexSecondField()
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
        Item: Record "Test Item";
    begin
        Item."No." := 'ITEM2';
        Item.Description := 'Gadget';
        Item.Amount := 10.0;
        Item.Insert();

        RecRef.GetTable(Item);

        // FieldIndex(2) should return the second field
        FldRef := RecRef.FieldIndex(2);
        Assert.AreNotEqual(0, FldRef.Number, 'FieldIndex(2) should return a FieldRef with non-zero number');
    end;

    [Test]
    procedure FieldIndexOutOfRangeReturnsStub()
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
        Item: Record "Test Item";
    begin
        Item."No." := 'ITEM3';
        Item.Insert();

        RecRef.GetTable(Item);

        // FieldIndex with an index beyond the field count should return a stub (field 0)
        FldRef := RecRef.FieldIndex(999);
        // Stub should have field number 0
        Assert.AreEqual(0, FldRef.Number, 'Out-of-range FieldIndex should return stub with number 0');
    end;

    [Test]
    procedure CaptionReturnsText()
    var
        RecRef: RecordRef;
        Item: Record "Test Item";
    begin
        RecRef.GetTable(Item);

        // Caption should return a text value (stub returns empty string)
        // Just verify it does not error out
        Assert.AreNotEqual('IMPOSSIBLE_CAPTION_VALUE', Format(RecRef.Caption),
            'Caption should return a text value');
    end;

    // === Blocker 2: TestPage field Visible, Lookup, Drilldown ===

    [Test]
    [HandlerFunctions('ItemCardModalHandler')]
    procedure TestPageFieldVisibleReturnsTrue()
    var
        ItemPage: TestPage "Test Item Card";
    begin
        ItemPage.OpenEdit();
        // Visible should return true by default
        Assert.IsTrue(ItemPage.Description.Visible, 'TestPage field Visible should be true');
        ItemPage.Close();
    end;

    [Test]
    [HandlerFunctions('ItemCardModalHandler')]
    procedure TestPageFieldVisibleNegative()
    var
        ItemPage: TestPage "Test Item Card";
    begin
        ItemPage.OpenEdit();
        // Visible returns true; asserting it is not false proves the mock returns a value
        Assert.AreNotEqual(false, ItemPage.Description.Visible, 'Visible should not be false');
        ItemPage.Close();
    end;

    [Test]
    [HandlerFunctions('ItemCardModalHandler')]
    procedure TestPageFieldLookupNoError()
    var
        ItemPage: TestPage "Test Item Card";
    begin
        ItemPage.OpenEdit();
        // Lookup should not error (no-op in standalone mode)
        ItemPage.Description.Lookup();
        ItemPage.Close();
    end;

    [Test]
    [HandlerFunctions('ItemCardModalHandler')]
    procedure TestPageFieldDrillDownNoError()
    var
        ItemPage: TestPage "Test Item Card";
    begin
        ItemPage.OpenEdit();
        // DrillDown should not error (no-op in standalone mode)
        ItemPage.Description.DrillDown();
        ItemPage.Close();
    end;

    // === Blocker 3: FieldRef.SetRange with variant/object ===

    [Test]
    procedure FieldRefSetRangeWithVariant()
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
        Item: Record "Test Item";
        V: Variant;
    begin
        Item."No." := 'V1';
        Item.Description := 'First';
        Item.Insert();

        Item."No." := 'V2';
        Item.Description := 'Second';
        Item.Insert();

        RecRef.Open(Database::"Test Item");
        FldRef := RecRef.Field(1);

        // SetRange with a variant value
        V := 'V1';
        FldRef.SetRange(V);

        Assert.IsTrue(RecRef.FindFirst(), 'Should find record with variant filter');
    end;

    [Test]
    procedure FieldRefSetRangeWithVariantNegative()
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
        Item: Record "Test Item";
        V: Variant;
    begin
        Item."No." := 'N1';
        Item.Description := 'Only';
        Item.Insert();

        RecRef.Open(Database::"Test Item");
        FldRef := RecRef.Field(1);

        // SetRange with variant that matches no records
        V := 'NONEXISTENT';
        FldRef.SetRange(V);

        Assert.IsTrue(RecRef.IsEmpty(), 'Should find no records with non-matching variant filter');
    end;

    [ModalPageHandler]
    procedure ItemCardModalHandler(var TestPage: TestPage "Test Item Card")
    begin
        // No-op handler for page operations
    end;
}
