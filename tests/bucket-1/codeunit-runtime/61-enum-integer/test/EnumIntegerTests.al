codeunit 57702 "EI Enum Integer Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Converter: Codeunit "EI Enum Converter";

    // -----------------------------------------------------------------------
    // AsInteger — enum value to integer ordinal
    // -----------------------------------------------------------------------

    [Test]
    procedure AsInteger_Red_ReturnsZero()
    begin
        // [GIVEN/WHEN] Red enum value converted to integer via helper
        // [THEN] ordinal is 0
        Assert.AreEqual(0, Converter.ToInteger(Enum::"EI Color"::"Red"), 'Red.AsInteger() must be 0');
    end;

    [Test]
    procedure AsInteger_Green_ReturnsOne()
    begin
        // [GIVEN/WHEN] Green enum value converted to integer via helper
        // [THEN] ordinal is 1
        Assert.AreEqual(1, Converter.ToInteger(Enum::"EI Color"::"Green"), 'Green.AsInteger() must be 1');
    end;

    [Test]
    procedure AsInteger_Blue_ReturnsTwo()
    begin
        // [GIVEN/WHEN] Blue enum value converted to integer via helper
        // [THEN] ordinal is 2
        Assert.AreEqual(2, Converter.ToInteger(Enum::"EI Color"::"Blue"), 'Blue.AsInteger() must be 2');
    end;

    [Test]
    procedure AsInteger_DirectCall_ReturnsOrdinal()
    var
        C: Enum "EI Color";
    begin
        // [GIVEN] Enum variable set to Green
        C := Enum::"EI Color"::"Green";

        // [WHEN] AsInteger called directly on variable (not via helper)
        // [THEN] returns correct ordinal
        Assert.AreEqual(1, C.AsInteger(), 'Direct C.AsInteger() must return ordinal 1 for Green');
    end;

    [Test]
    procedure AsInteger_AllValuesAreDistinct()
    var
        R: Integer;
        G: Integer;
        B: Integer;
    begin
        // [GIVEN] All three enum values
        // [WHEN] AsInteger called on each
        R := Converter.ToInteger(Enum::"EI Color"::"Red");
        G := Converter.ToInteger(Enum::"EI Color"::"Green");
        B := Converter.ToInteger(Enum::"EI Color"::"Blue");

        // [THEN] All ordinals are distinct
        Assert.AreNotEqual(R, G, 'Red and Green ordinals must differ');
        Assert.AreNotEqual(G, B, 'Green and Blue ordinals must differ');
        Assert.AreNotEqual(R, B, 'Red and Blue ordinals must differ');
    end;

    [Test]
    procedure AsInteger_DefaultEnum_ReturnsZero()
    var
        C: Enum "EI Color";
    begin
        // [GIVEN] A freshly declared enum variable (default value)
        // [WHEN] AsInteger called
        // [THEN] default enum value has ordinal 0 (first declared value = Red)
        Assert.AreEqual(0, C.AsInteger(), 'Default enum variable must return ordinal 0');
    end;
}
