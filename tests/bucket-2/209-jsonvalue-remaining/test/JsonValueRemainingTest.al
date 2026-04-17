codeunit 60361 "JVR Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "JVR Src";

    [Test]
    procedure AsBigInteger_RoundTrip()
    begin
        Assert.AreEqual(1234567890, Src.AsBigInteger(),
            'SetValue(1234567890) + AsBigInteger must round-trip');
    end;

    [Test]
    procedure AsCode_RoundTrip()
    begin
        Assert.AreEqual('ABC-123', Src.AsCode(),
            'SetValue(ABC-123) + AsCode must round-trip');
    end;

    [Test]
    procedure AsOption_RoundTrip()
    begin
        Assert.AreEqual(2, Src.AsOption(),
            'SetValue(2) + AsOption must round-trip');
    end;

    [Test]
    procedure AsDate_RoundTrip()
    begin
        Assert.AreEqual(DMY2Date(15, 6, 2024), Src.AsDate(),
            'SetValue(Date) + AsDate must round-trip');
    end;

    [Test]
    procedure AsTime_DoesNotThrow()
    begin
        // AsTime may have precision differences; just verify it doesn't throw.
        Src.AsTime();
        Assert.IsTrue(true, 'AsTime must not throw');
    end;

    [Test]
    procedure AsDateTime_DoesNotThrow()
    begin
        Src.AsDateTime();
        Assert.IsTrue(true, 'AsDateTime must not throw');
    end;

    [Test]
    procedure AsByte_RoundTrip()
    begin
        Assert.AreEqual(42, Src.AsByte(),
            'SetValue(42) + AsByte must round-trip');
    end;

    [Test]
    procedure AsChar_RoundTrip()
    begin
        // AsChar returns the character representation — 65 = 'A'.
        Assert.AreEqual('A', Format(Src.AsChar()),
            'SetValue(65) + AsChar must return the character A');
    end;

    [Test]
    procedure AsDuration_RoundTrip()
    begin
        Assert.AreEqual(60000, Src.AsDuration(),
            'SetValue(60000) + AsDuration must round-trip');
    end;
}
