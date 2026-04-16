codeunit 50133 "Test EFI From Integer"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Conv: Codeunit "EFI Converter";

    [Test]
    procedure FromInteger_Zero_ReturnsOpen()
    begin
        // [GIVEN] Integer ordinal 0
        // [WHEN] Enum::"EFI Status".FromInteger(0) is called via helper
        // [THEN] Returns Open
        Assert.AreEqual(Enum::"EFI Status"::Open, Conv.FromInt(0),
            'FromInteger(0) must return Open');
    end;

    [Test]
    procedure FromInteger_One_ReturnsReleased()
    begin
        // [GIVEN] Integer ordinal 1
        // [WHEN] FromInteger(1) is called
        // [THEN] Returns Released
        Assert.AreEqual(Enum::"EFI Status"::Released, Conv.FromInt(1),
            'FromInteger(1) must return Released');
    end;

    [Test]
    procedure FromInteger_Two_ReturnsClosed()
    begin
        // [GIVEN] Integer ordinal 2
        // [WHEN] FromInteger(2) is called
        // [THEN] Returns Closed
        Assert.AreEqual(Enum::"EFI Status"::Closed, Conv.FromInt(2),
            'FromInteger(2) must return Closed');
    end;

    [Test]
    procedure FromInteger_RoundTrip_WithAsInteger()
    var
        Original: Enum "EFI Status";
        Ordinal: Integer;
        Restored: Enum "EFI Status";
    begin
        // [GIVEN] An enum value
        Original := Enum::"EFI Status"::Released;

        // [WHEN] Converting to integer and back
        Ordinal := Original.AsInteger();
        Restored := Conv.FromInt(Ordinal);

        // [THEN] The restored value equals the original
        Assert.AreEqual(Original, Restored,
            'FromInteger(AsInteger()) must round-trip to original value');
    end;

    [Test]
    procedure FromInteger_OutOfRange_RaisesError()
    begin
        // [GIVEN] An ordinal that has no enum value
        // [WHEN] FromInteger is called with 99
        // [THEN] A runtime error is raised
        asserterror Conv.FromInt(99);
        Assert.ExpectedError('');
    end;
}
