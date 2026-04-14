codeunit 56231 "FE Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Probe: Codeunit "FE Probe";

    // === FieldRef.IsEnum ===

    [Test]
    procedure IsEnumReturnsTrueForEnumField()
    begin
        // [GIVEN] Table 56230 has field 3 = Enum "FE Color"
        // [WHEN] FieldRef.IsEnum is called on field 3
        // [THEN] Returns true
        Assert.IsTrue(Probe.FieldRefIsEnum(56230, 3), 'Enum field should report IsEnum = true');
    end;

    [Test]
    procedure IsEnumReturnsFalseForNonEnumField()
    begin
        // [GIVEN] Table 56230 has field 2 = Text[100]
        // [WHEN] FieldRef.IsEnum is called on field 2
        // [THEN] Returns false
        Assert.IsFalse(Probe.FieldRefIsEnum(56230, 2), 'Text field should report IsEnum = false');
    end;

    // === FieldRef.EnumValueCount ===

    [Test]
    procedure EnumValueCountReturnsFour()
    begin
        // [GIVEN] Enum "FE Color" has 4 values: " ", Red, Green, Blue
        // [WHEN] FieldRef.EnumValueCount is called on field 3
        // [THEN] Returns 4
        Assert.AreEqual(4, Probe.FieldRefEnumValueCount(56230, 3), 'FE Color has 4 members');
    end;

    [Test]
    procedure EnumValueCountReturnsZeroForNonEnum()
    begin
        // [GIVEN] Field 2 is Text, not an enum
        // [WHEN] FieldRef.EnumValueCount is called on field 2
        // [THEN] Returns 0
        Assert.AreEqual(0, Probe.FieldRefEnumValueCount(56230, 2), 'Text field has 0 enum members');
    end;

    // === FieldRef.GetEnumValueName ===

    [Test]
    procedure GetEnumValueNameReturnsCorrectNames()
    begin
        // [GIVEN] Enum "FE Color" has values: " "(0), Red(1), Green(5), Blue(10)
        // [WHEN] GetEnumValueName is called with 1-based indices
        // [THEN] Returns the correct name for each index
        Assert.AreEqual(' ', Probe.FieldRefGetEnumValueName(56230, 3, 1), 'Index 1 should be empty');
        Assert.AreEqual('Red', Probe.FieldRefGetEnumValueName(56230, 3, 2), 'Index 2 should be Red');
        Assert.AreEqual('Green', Probe.FieldRefGetEnumValueName(56230, 3, 3), 'Index 3 should be Green');
        Assert.AreEqual('Blue', Probe.FieldRefGetEnumValueName(56230, 3, 4), 'Index 4 should be Blue');
    end;

    // === FieldRef.GetEnumValueCaption ===

    [Test]
    procedure GetEnumValueCaptionReturnsCorrectCaptions()
    begin
        // Caption defaults to name when no CaptionML is specified
        Assert.AreEqual(' ', Probe.FieldRefGetEnumValueCaption(56230, 3, 1), 'Caption index 1');
        Assert.AreEqual('Red', Probe.FieldRefGetEnumValueCaption(56230, 3, 2), 'Caption index 2');
        Assert.AreEqual('Green', Probe.FieldRefGetEnumValueCaption(56230, 3, 3), 'Caption index 3');
        Assert.AreEqual('Blue', Probe.FieldRefGetEnumValueCaption(56230, 3, 4), 'Caption index 4');
    end;

    // === FieldRef.GetEnumValueOrdinal ===

    [Test]
    procedure GetEnumValueOrdinalReturnsCorrectOrdinals()
    begin
        // Ordinals: " " = 0, Red = 1, Green = 5, Blue = 10
        Assert.AreEqual(0, Probe.FieldRefGetEnumValueOrdinal(56230, 3, 1), 'Ordinal index 1');
        Assert.AreEqual(1, Probe.FieldRefGetEnumValueOrdinal(56230, 3, 2), 'Ordinal index 2');
        Assert.AreEqual(5, Probe.FieldRefGetEnumValueOrdinal(56230, 3, 3), 'Ordinal index 3');
        Assert.AreEqual(10, Probe.FieldRefGetEnumValueOrdinal(56230, 3, 4), 'Ordinal index 4');
    end;

    // === FieldRef.CalcSum ===

    [Test]
    procedure CalcSumReturnsCorrectTotal()
    var
        Item: Record "FE Test Item";
    begin
        // [GIVEN] Three records with Price = 10.50, 20.25, 30.75
        Item.Init();
        Item."Id" := 1;
        Item."Price" := 10.50;
        Item.Insert();

        Item.Init();
        Item."Id" := 2;
        Item."Price" := 20.25;
        Item.Insert();

        Item.Init();
        Item."Id" := 3;
        Item."Price" := 30.75;
        Item.Insert();

        // [WHEN] CalcSum is called on field 4 (Price)
        // [THEN] Returns 61.50
        Assert.AreEqual(61.50, Probe.CalcSumPrice(56230, 4), 'Sum of 10.50 + 20.25 + 30.75');
    end;

    [Test]
    procedure CalcSumReturnsZeroForEmptyTable()
    begin
        // [GIVEN] No records exist
        // [WHEN] CalcSum on Price field
        // [THEN] Returns 0
        Assert.AreEqual(0, Probe.CalcSumPrice(56230, 4), 'Empty table sum should be 0');
    end;

    // === RecordRef system-field number accessors ===

    [Test]
    procedure SystemFieldNosReturnCorrectValues()
    begin
        // The BC well-known system field numbers
        Assert.AreEqual('2000000000,2000000001,2000000002,2000000003,2000000004',
            Probe.SystemFieldNos(), 'System field numbers');
    end;

    // === Record.WritePermission ===

    [Test]
    procedure WritePermissionReturnsTrue()
    var
        Item: Record "FE Test Item";
    begin
        // In standalone mode, WritePermission always returns true
        Assert.IsTrue(Probe.WritePermissionTest(Item), 'WritePermission should be true');
    end;
}
