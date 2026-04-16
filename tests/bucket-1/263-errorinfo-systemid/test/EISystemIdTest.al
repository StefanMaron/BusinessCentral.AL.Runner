codeunit 61931 "EI SystemId Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ------------------------------------------------------------------
    // Positive: SystemId round-trips correctly.
    // ------------------------------------------------------------------

    [Test]
    procedure SystemId_RoundTrip()
    var
        Src: Codeunit "EI SystemId Src";
    begin
        // [GIVEN/WHEN] A new GUID is set on ErrorInfo.SystemId
        // [THEN]  The same non-null GUID is returned from SystemId()
        Assert.IsTrue(not IsNullGuid(Src.SetAndGet()), 'SystemId round-trip must not be null GUID');
    end;

    [Test]
    procedure SystemId_SpecificValue_RoundTrips()
    var
        Src: Codeunit "EI SystemId Src";
        g: Guid;
        result: Guid;
    begin
        // [GIVEN] A specific GUID
        g := '{12345678-1234-1234-1234-123456789ABC}';
        // [WHEN]  It is set as SystemId
        result := Src.SetAndGetSpecific(g);
        // [THEN]  The exact same GUID is returned — proving value is stored and retrieved
        Assert.AreEqual(Format(g), Format(result), 'SystemId must return the exact GUID that was set');
    end;

    // ------------------------------------------------------------------
    // Negative: default SystemId is null GUID.
    // ------------------------------------------------------------------

    [Test]
    procedure SystemId_DefaultIsNullGuid()
    var
        Src: Codeunit "EI SystemId Src";
    begin
        // [GIVEN] An ErrorInfo with no SystemId set
        // [THEN]  SystemId() returns a null GUID
        Assert.IsTrue(IsNullGuid(Src.GetDefault()), 'Default SystemId must be null GUID');
    end;

    [Test]
    procedure SystemId_NotSameAsOtherRoundTrip()
    var
        Src: Codeunit "EI SystemId Src";
        g1: Guid;
        g2: Guid;
    begin
        // [GIVEN] Two different GUIDs set on separate ErrorInfo variables
        g1 := '{11111111-1111-1111-1111-111111111111}';
        g2 := '{22222222-2222-2222-2222-222222222222}';
        // [THEN]  Each returns its own value — they are independent
        Assert.AreEqual(Format(g1), Format(Src.SetAndGetSpecific(g1)), 'First GUID must match');
        Assert.AreEqual(Format(g2), Format(Src.SetAndGetSpecific(g2)), 'Second GUID must match');
        Assert.AreNotEqual(Format(g1), Format(g2), 'GUIDs must be distinct');
    end;
}
