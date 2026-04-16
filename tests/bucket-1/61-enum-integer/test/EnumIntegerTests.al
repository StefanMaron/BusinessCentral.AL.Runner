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
        // [GIVEN/WHEN] Red enum value converted to integer
        // [THEN] ordinal is 0
        Assert.AreEqual(0, Converter.ToInteger(Enum::"EI Color"::"Red"), 'Red.AsInteger() must be 0');
    end;

    [Test]
    procedure AsInteger_Green_ReturnsOne()
    begin
        // [GIVEN/WHEN] Green enum value converted to integer
        // [THEN] ordinal is 1
        Assert.AreEqual(1, Converter.ToInteger(Enum::"EI Color"::"Green"), 'Green.AsInteger() must be 1');
    end;

    [Test]
    procedure AsInteger_Blue_ReturnsTwo()
    begin
        // [GIVEN/WHEN] Blue enum value converted to integer
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

        // [WHEN] AsInteger called directly on variable
        // [THEN] returns correct ordinal
        Assert.AreEqual(1, C.AsInteger(), 'Direct C.AsInteger() must return ordinal 1 for Green');
    end;

    // -----------------------------------------------------------------------
    // FromInteger — integer to enum value
    // -----------------------------------------------------------------------

    [Test]
    procedure FromInteger_Zero_ReturnsRed()
    begin
        // [GIVEN/WHEN] integer 0 converted to enum
        // [THEN] returns Red
        Assert.AreEqual(Enum::"EI Color"::"Red", Converter.FromInteger(0), 'FromInteger(0) must return Red');
    end;

    [Test]
    procedure FromInteger_One_ReturnsGreen()
    begin
        // [GIVEN/WHEN] integer 1 converted to enum
        // [THEN] returns Green
        Assert.AreEqual(Enum::"EI Color"::"Green", Converter.FromInteger(1), 'FromInteger(1) must return Green');
    end;

    [Test]
    procedure FromInteger_Two_ReturnsBlue()
    begin
        // [GIVEN/WHEN] integer 2 converted to enum
        // [THEN] returns Blue
        Assert.AreEqual(Enum::"EI Color"::"Blue", Converter.FromInteger(2), 'FromInteger(2) must return Blue');
    end;

    [Test]
    procedure FromInteger_RoundTrip_PreservesValue()
    var
        Original: Enum "EI Color";
        RoundTripped: Enum "EI Color";
    begin
        // [GIVEN] an enum value
        Original := Enum::"EI Color"::"Blue";

        // [WHEN] converted to integer and back
        RoundTripped := Enum::"EI Color".FromInteger(Original.AsInteger());

        // [THEN] round-trip preserves the value
        Assert.AreEqual(Original, RoundTripped, 'AsInteger + FromInteger round-trip must preserve enum value');
    end;

    // -----------------------------------------------------------------------
    // FromInteger — invalid ordinal must error
    // -----------------------------------------------------------------------

    [Test]
    procedure FromInteger_InvalidOrdinal_Errors()
    begin
        // [GIVEN] an ordinal not in the enum definition
        // [WHEN/THEN] FromInteger must throw a runtime error
        asserterror Enum::"EI Color".FromInteger(99);
        Assert.ExpectedError('');
    end;
}
