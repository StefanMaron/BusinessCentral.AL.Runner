/// Tests that reading an enum/option field before it has been explicitly set
/// does not crash with "Value cannot be null (Parameter 'key')" in
/// AlCompat.CloneTaggedOption.
///
/// Background: when a record is created with Init() only, the underlying
/// NavOption for every enum field is null.  Any subsequent re-assignment of
/// that field to a local enum variable triggers CloneTaggedOption(null, ordinal)
/// which previously threw a NullReferenceException / ArgumentNullException.
codeunit 29901 "NullOpt Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // -------------------------------------------------------------------------
    // Positive: reading an uninitialized enum field yields ordinal 0 (default)
    // -------------------------------------------------------------------------

    [Test]
    procedure ReadUninitializedEnumField_ReturnsDefaultOrdinal()
    var
        Rec:    Record "NullOpt Record";
        Helper: Codeunit "NullOpt Helper";
        Result: Integer;
    begin
        // [GIVEN] A record created with Init() — Status field never assigned
        Rec.Init();
        Rec.Id := 1;
        Rec.Insert(false);

        // [WHEN] The Status field is copied to a local enum variable
        //        (triggers CloneTaggedOption with null existing)
        Result := Helper.ReadUninitializedStatus(Rec);

        // [THEN] Ordinal must be 0 — the BC-default for uninitialized enums
        Assert.AreEqual(0, Result, 'Uninitialized enum field must yield ordinal 0');
    end;

    // -------------------------------------------------------------------------
    // Positive: re-assigning a non-default value still returns the correct ordinal
    // -------------------------------------------------------------------------

    [Test]
    procedure ReadInitializedEnumField_ReturnsCorrectOrdinal()
    var
        Rec:    Record "NullOpt Record";
        Helper: Codeunit "NullOpt Helper";
        Result: Integer;
    begin
        // [GIVEN] A freshly Init()ed record (Status null)
        Rec.Init();
        Rec.Id := 2;
        Rec.Insert(false);

        // [WHEN] Status is set to Closed (ordinal 2) and then cloned
        Result := Helper.ReadInitializedStatus(Rec, Rec.Status::Closed);

        // [THEN] Ordinal must be 2
        Assert.AreEqual(2, Result, 'Initialized enum field must yield the assigned ordinal');
    end;

    // -------------------------------------------------------------------------
    // Negative: a truly invalid ordinal should not be silently returned as 0
    // — prove the positive tests would catch a no-op stub
    // -------------------------------------------------------------------------

    [Test]
    procedure ReadInitializedEnumField_ActiveOrdinalIs1()
    var
        Rec:    Record "NullOpt Record";
        Helper: Codeunit "NullOpt Helper";
        Result: Integer;
    begin
        Rec.Init();
        Rec.Id := 3;
        Rec.Insert(false);

        Result := Helper.ReadInitializedStatus(Rec, Rec.Status::Active);

        // Ordinal 1 — a no-op stub returning 0 would fail this assertion
        Assert.AreEqual(1, Result, 'Active enum must yield ordinal 1');
    end;
}
