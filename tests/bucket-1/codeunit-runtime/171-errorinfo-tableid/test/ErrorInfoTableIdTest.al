codeunit 59921 "EIT Tid Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "EIT Tid Src";

    [Test]
    procedure TableId_SetAndGet()
    begin
        Assert.AreEqual(18, Src.SetAndGet(18), 'TableId must round-trip 18');
    end;

    [Test]
    procedure TableId_Zero()
    begin
        // 0 is a valid TableId value (fresh default).
        Assert.AreEqual(0, Src.SetAndGet(0), 'TableId must round-trip 0');
    end;

    [Test]
    procedure TableId_Fresh_IsZero()
    begin
        // Default-initialised ErrorInfo has TableId = 0.
        Assert.AreEqual(0, Src.FreshTableId(), 'Fresh ErrorInfo TableId must be 0');
    end;

    [Test]
    procedure TableId_LastWriteWins()
    begin
        Assert.AreEqual(27, Src.LastWriteWins(18, 27),
            'TableId setter must overwrite; last write wins');
    end;

    [Test]
    procedure TableId_LargeValue()
    begin
        // Large valid table IDs must round-trip.
        Assert.AreEqual(50000, Src.SetAndGet(50000), 'TableId 50000 must round-trip');
        Assert.AreEqual(2000000000, Src.SetAndGet(2000000000), 'TableId near INT_MAX must round-trip');
    end;

    [Test]
    procedure TableId_DifferentInputs_DifferentOutputs_NegativeTrap()
    begin
        Assert.AreNotEqual(
            Src.SetAndGet(18),
            Src.SetAndGet(27),
            'Different TableId inputs must produce different outputs');
    end;
}
