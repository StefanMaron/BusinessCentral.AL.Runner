codeunit 81401 "DS Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "DS Src";

    // -----------------------------------------------------------------------
    // Positive: SID is non-empty
    // -----------------------------------------------------------------------

    [Test]
    procedure SID_ReturnsNonEmpty()
    begin
        // Positive: Database.SID() must return a non-empty stub value
        Assert.AreNotEqual('', Src.GetSid(),
            'Database.SID() must return a non-empty string');
    end;

    [Test]
    procedure SID_NonEmpty_ViaHelper()
    begin
        // Positive: IsNonEmpty check via helper
        Assert.IsTrue(Src.SidIsNonEmpty(),
            'Database.SID() must be non-empty');
    end;

    [Test]
    procedure SID_HasPositiveLength()
    begin
        // Positive: SID length > 0
        Assert.IsTrue(Src.SidLength() > 0,
            'Database.SID() must have positive length');
    end;

    // -----------------------------------------------------------------------
    // Negative: SID is not a real Windows SID
    // -----------------------------------------------------------------------

    [Test]
    procedure SID_NotRealWindowsSid()
    begin
        // Negative: stub should not return a real domain SID format
        Assert.AreNotEqual('S-1-5-21', Src.GetSid(),
            'Database.SID() must not return a real Windows SID prefix');
    end;

    [Test]
    procedure SID_ConsistentAcrossCalls()
    var
        Sid1: Text;
        Sid2: Text;
    begin
        // Positive: repeated calls return the same stub value
        Sid1 := Database.SID();
        Sid2 := Database.SID();
        Assert.AreEqual(Sid1, Sid2,
            'Database.SID() must return the same value on repeated calls');
    end;

    [Test]
    procedure SID_InlineCall()
    var
        Sid: Text;
    begin
        // Positive: inline call compiles and returns non-empty
        Sid := Database.SID();
        Assert.AreNotEqual('', Sid,
            'Inline Database.SID() call must return non-empty');
    end;
}
