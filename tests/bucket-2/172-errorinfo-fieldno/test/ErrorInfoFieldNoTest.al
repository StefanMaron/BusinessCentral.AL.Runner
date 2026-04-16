codeunit 59931 "EIF Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "EIF Src";

    [Test]
    procedure FieldNo_SetAndGet()
    begin
        Assert.AreEqual(3, Src.SetAndGet(3), 'FieldNo must round-trip 3');
    end;

    [Test]
    procedure FieldNo_Zero()
    begin
        Assert.AreEqual(0, Src.SetAndGet(0), 'FieldNo must round-trip 0');
    end;

    [Test]
    procedure FieldNo_Fresh_IsZero()
    begin
        Assert.AreEqual(0, Src.FreshFieldNo(), 'Fresh ErrorInfo FieldNo must be 0');
    end;

    [Test]
    procedure FieldNo_LastWriteWins()
    begin
        Assert.AreEqual(7, Src.LastWriteWins(3, 7),
            'FieldNo setter must overwrite; last write wins');
    end;

    [Test]
    procedure FieldNo_LargeValues()
    begin
        Assert.AreEqual(10000, Src.SetAndGet(10000), 'FieldNo 10000 must round-trip');
        Assert.AreEqual(2000000000, Src.SetAndGet(2000000000), 'FieldNo near INT_MAX must round-trip');
    end;

    [Test]
    procedure FieldNo_DifferentInputs_DifferentOutputs_NegativeTrap()
    begin
        Assert.AreNotEqual(
            Src.SetAndGet(3),
            Src.SetAndGet(7),
            'Different FieldNo inputs must produce different outputs');
    end;
}
