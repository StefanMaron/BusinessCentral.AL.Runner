codeunit 61951 "EI DataClass Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ------------------------------------------------------------------
    // Positive: DataClassification compiles and round-trips correctly.
    // ------------------------------------------------------------------

    [Test]
    procedure DataClassification_Compiles()
    var
        Src: Codeunit "EI DataClass Src";
    begin
        // [GIVEN/WHEN] DataClassification::CustomerContent is set — no error expected
        Src.SetCustomerContent();
        // [THEN]  Execution completes without error
        Assert.IsTrue(true, 'Setting DataClassification::CustomerContent must not raise an error');
    end;

    [Test]
    procedure DataClassification_RoundTrip()
    var
        Src: Codeunit "EI DataClass Src";
    begin
        // [GIVEN/WHEN] CustomerContent is set and retrieved
        // [THEN]  The retrieved value equals CustomerContent
        Assert.IsTrue(Src.CustomerContentRoundTrips(), 'DataClassification::CustomerContent must round-trip');
    end;

    [Test]
    procedure DataClassification_TwoValuesAreDistinct()
    var
        Src: Codeunit "EI DataClass Src";
    begin
        // [THEN] CustomerContent and SystemMetadata are stored independently
        Assert.IsTrue(Src.TwoValuesAreDistinct(),
            'Two different DataClassification values must not compare equal');
    end;

    // ------------------------------------------------------------------
    // Negative: default DataClassification is NOT CustomerContent.
    // ------------------------------------------------------------------

    [Test]
    procedure DataClassification_DefaultIsNotCustomerContent()
    var
        Src: Codeunit "EI DataClass Src";
    begin
        // [GIVEN] An ErrorInfo with no DataClassification set
        // [THEN]  Default is NOT CustomerContent — a no-op mock would fail this
        Assert.IsFalse(Src.DefaultMatchesNone(),
            'Default DataClassification must not equal CustomerContent');
    end;
}
