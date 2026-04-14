codeunit 50911 "Variant Type Tests"
{
    Subtype = Test;

    var
        VariantHelper: Codeunit "Variant Helper";
        Assert: Codeunit Assert;

    [Test]
    procedure TestVariantHoldsInteger()
    var
        V: Variant;
    begin
        // [GIVEN/WHEN] An integer is computed and stored in a Variant via another codeunit
        VariantHelper.AddToVariant(10, 25, V);

        // [THEN] Format should show the sum
        Assert.AreEqual('35', Format(V), 'Variant should hold sum 35 as formatted text');
    end;

    [Test]
    procedure TestVariantHoldsText()
    var
        V: Variant;
    begin
        // [GIVEN/WHEN] Text is concatenated and stored in a Variant
        VariantHelper.ConcatToVariant('Hello', ' World', V);

        // [THEN] Format should show concatenated text
        Assert.AreEqual('Hello World', Format(V), 'Variant should hold concatenated text');
    end;

    [Test]
    procedure TestVariantHoldsDecimal()
    var
        V: Variant;
    begin
        // [GIVEN/WHEN] A decimal is doubled and stored in a Variant
        VariantHelper.DoubleDecimal(12.50, V);

        // [THEN] Format should show the doubled value
        Assert.AreEqual('25', Format(V), 'Variant should hold doubled decimal 25');
    end;

    [Test]
    procedure TestVariantHoldsBoolean()
    var
        V: Variant;
    begin
        // [GIVEN/WHEN] A boolean is negated and stored in a Variant
        VariantHelper.NegateBoolean(true, V);

        // [THEN] Format should show False
        Assert.AreEqual('False', Format(V), 'Variant should hold negated boolean (False)');
    end;

    [Test]
    procedure TestVariantPassThroughPreservesValue()
    var
        V: Variant;
    begin
        // [GIVEN/WHEN] Multiple operations on variant
        VariantHelper.AddToVariant(100, 200, V);
        Assert.AreEqual('300', Format(V), 'First pass-through should hold 300');

        // [WHEN] Overwriting with different value
        VariantHelper.AddToVariant(1, 1, V);
        Assert.AreEqual('2', Format(V), 'Second pass-through should overwrite with 2');
    end;

    [Test]
    procedure TestFormatInteger()
    var
        V: Variant;
    begin
        // [GIVEN] A simple integer assigned to variant
        V := 42;

        // [WHEN/THEN] FormatAsText returns formatted value
        Assert.AreEqual('42', VariantHelper.FormatAsText(V), 'FormatAsText should return 42');
    end;

    [Test]
    procedure TestFormatText()
    var
        V: Variant;
    begin
        V := 'Test String';
        Assert.AreEqual('Test String', VariantHelper.FormatAsText(V), 'FormatAsText should return the text');
    end;
}
