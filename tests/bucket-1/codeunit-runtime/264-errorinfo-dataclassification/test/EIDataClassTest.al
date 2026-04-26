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
    procedure DataClassification_CustomerContent_RoundTrips()
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
        // [THEN] CustomerContent and SystemMetadata are stored independently and compare unequal
        Assert.IsTrue(Src.TwoValuesAreDistinct(),
            'CustomerContent and SystemMetadata must be distinct values');
    end;

    // ------------------------------------------------------------------
    // Negative: a second distinct value also round-trips correctly.
    // ------------------------------------------------------------------

    [Test]
    procedure DataClassification_SystemMetadata_RoundTrips()
    var
        Src: Codeunit "EI DataClass Src";
    begin
        // [GIVEN/WHEN] SystemMetadata is set and retrieved
        // [THEN]  The retrieved value equals SystemMetadata — a mock returning
        //         CustomerContent unconditionally would fail this
        Assert.IsTrue(Src.SystemMetadataRoundTrips(),
            'DataClassification::SystemMetadata must round-trip correctly');
    end;
}
