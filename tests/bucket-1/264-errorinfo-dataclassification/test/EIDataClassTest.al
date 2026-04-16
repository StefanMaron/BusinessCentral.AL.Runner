codeunit 61951 "EI DataClass Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ------------------------------------------------------------------
    // Positive: DataClassification round-trips correctly.
    // ------------------------------------------------------------------

    [Test]
    procedure DataClassification_RoundTrip()
    var
        Src: Codeunit "EI DataClass Src";
    begin
        // [GIVEN/WHEN] DataClassification::CustomerContent is set and retrieved
        // [THEN]  The integer value is non-zero — CustomerContent is not the default (0)
        Assert.AreNotEqual(0, Src.SetAndGet(), 'DataClassification must not be zero after set');
    end;

    [Test]
    procedure DataClassification_CustomerContent_SpecificValue()
    var
        Src: Codeunit "EI DataClass Src";
        Expected: Integer;
    begin
        // [GIVEN] DataClassification::CustomerContent has a known integer value
        Expected := DataClassification::CustomerContent.AsInteger();
        // [WHEN/THEN] Setting and retrieving CustomerContent returns the exact integer
        Assert.AreEqual(Expected, Src.SetAndGet(), 'CustomerContent must round-trip to its enum integer');
    end;

    [Test]
    procedure DataClassification_TwoValuesAreDistinct()
    var
        Src: Codeunit "EI DataClass Src";
    begin
        // [THEN] CustomerContent and EndUserIdentifiableInformation produce different integers
        Assert.AreNotEqual(Src.SetAndGet(), Src.SetEndUserContent(),
            'Two different DataClassification values must produce distinct integers');
    end;

    // ------------------------------------------------------------------
    // Negative: default DataClassification is 0.
    // ------------------------------------------------------------------

    [Test]
    procedure DataClassification_DefaultIsZero()
    var
        Src: Codeunit "EI DataClass Src";
    begin
        // [GIVEN] An ErrorInfo with no DataClassification set
        // [THEN]  DataClassification() returns integer 0 (unset / default)
        Assert.AreEqual(0, Src.GetDefault(), 'Default DataClassification must be 0');
    end;
}
