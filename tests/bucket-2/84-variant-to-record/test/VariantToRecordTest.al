codeunit 84901 "Variant To Record Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "Variant Record Helper";

    [Test]
    procedure TestVariantToRecordExtractsDescription()
    var
        VRItem: Record "VR Item";
        RecVariant: Variant;
    begin
        // [GIVEN] A record with data
        VRItem."No." := 'ITEM-001';
        VRItem.Description := 'Test Item Description';
        VRItem.Quantity := 5;
        VRItem.Insert();

        // [WHEN] Record is wrapped in a Variant and then cast back to Record
        Helper.WrapRecordInVariant(VRItem, RecVariant);

        // [THEN] Extracting description from the Variant works correctly
        Assert.AreEqual('Test Item Description', Helper.ExtractDescriptionFromVariant(RecVariant),
            'Description should be extractable from Variant-wrapped record');
    end;

    [Test]
    procedure TestVariantToRecordPreservesQuantity()
    var
        VRItem: Record "VR Item";
        RecVariant: Variant;
    begin
        // [GIVEN] A record with a decimal field
        VRItem."No." := 'ITEM-002';
        VRItem.Description := 'Widget';
        VRItem.Quantity := 42.5;
        VRItem.Insert();

        // [WHEN] Record is wrapped in a Variant
        Helper.WrapRecordInVariant(VRItem, RecVariant);

        // [THEN] Quantity field is preserved through Variant round-trip
        Assert.AreEqual(42.5, Helper.GetQuantityFromVariant(RecVariant),
            'Quantity should be preserved through Variant cast');
    end;

    [Test]
    procedure TestVariantIsRecord()
    var
        VRItem: Record "VR Item";
        RecVariant: Variant;
    begin
        // [GIVEN] A record wrapped in a Variant
        VRItem."No." := 'ITEM-003';
        VRItem.Insert();
        Helper.WrapRecordInVariant(VRItem, RecVariant);

        // [THEN] Variant.IsRecord returns true
        Assert.IsTrue(RecVariant.IsRecord(), 'Variant holding a record should report IsRecord = true');
    end;

    [Test]
    procedure TestVariantIsRecordFalseForText()
    var
        RecVariant: Variant;
    begin
        // [GIVEN] A Variant holding text
        RecVariant := 'not a record';

        // [THEN] Variant.IsRecord returns false
        Assert.IsFalse(RecVariant.IsRecord(), 'Variant holding text should report IsRecord = false');
    end;

    // ── Nav-wrapper type-check tests ──────────────────────────────────────────
    // Values read from record fields come back as Nav runtime wrappers
    // (NavBoolean, NavInteger, NavDecimal, etc.). Variant.IsXxx() must return
    // true for these, not just for raw CLR types.

    [Test]
    procedure TestVariantIsBooleanForRecordField()
    var
        VRItem: Record "VR Item";
        V: Variant;
    begin
        // [GIVEN] A Boolean field value stored in a Variant via record field read
        VRItem."No." := 'BOOL-001';
        VRItem.Active := true;
        VRItem.Insert();
        VRItem.Get('BOOL-001');
        V := VRItem.Active;

        // [THEN] IsBoolean returns true even though the wrapper is NavBoolean
        Assert.IsTrue(V.IsBoolean(), 'Variant holding a Boolean field value should report IsBoolean = true');
    end;

    [Test]
    procedure TestVariantIsIntegerForRecordField()
    var
        VRItem: Record "VR Item";
        V: Variant;
    begin
        // [GIVEN] An Integer field value stored in a Variant
        VRItem."No." := 'INT-001';
        VRItem.Count := 7;
        VRItem.Insert();
        VRItem.Get('INT-001');
        V := VRItem.Count;

        // [THEN] IsInteger returns true even though the wrapper is NavInteger
        Assert.IsTrue(V.IsInteger(), 'Variant holding an Integer field value should report IsInteger = true');
    end;

    [Test]
    procedure TestVariantIsDecimalForRecordField()
    var
        VRItem: Record "VR Item";
        V: Variant;
    begin
        // [GIVEN] A Decimal field value stored in a Variant
        VRItem."No." := 'DEC-001';
        VRItem.Quantity := 3.14;
        VRItem.Insert();
        VRItem.Get('DEC-001');
        V := VRItem.Quantity;

        // [THEN] IsDecimal returns true even though the wrapper is Decimal18/NavDecimal
        Assert.IsTrue(V.IsDecimal(), 'Variant holding a Decimal field value should report IsDecimal = true');
    end;

    [Test]
    procedure TestDirectVariantAssignToRecord()
    var
        VRItem: Record "VR Item";
        VRItem2: Record "VR Item";
        RecVariant: Variant;
    begin
        // [GIVEN] Insert a record and wrap it in a Variant directly
        VRItem."No." := 'ITEM-004';
        VRItem.Description := 'Direct Assign';
        VRItem.Quantity := 10;
        VRItem.Insert();

        RecVariant := VRItem;

        // [WHEN] Assign from Variant back to another Record variable (direct cast in AL)
        VRItem2 := RecVariant;

        // [THEN] The second record has the same data
        Assert.AreEqual('ITEM-004', VRItem2."No.", 'No. should match after Variant-to-Record cast');
        Assert.AreEqual('Direct Assign', VRItem2.Description, 'Description should match after Variant cast');
        Assert.AreEqual(10, VRItem2.Quantity, 'Quantity should match after Variant cast');
    end;
}
