codeunit 59911 "EID Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "EID Src";

    [Test]
    procedure DetailedMessage_SetAndGet_RoundsTrip()
    begin
        // Positive: DetailedMessage getter returns what the setter set.
        Assert.AreEqual(
            'See the audit log for details.',
            Src.SetAndGet('See the audit log for details.'),
            'DetailedMessage must round-trip the set value');
    end;

    [Test]
    procedure DetailedMessage_EmptyString()
    begin
        // Edge: empty string round-trips cleanly.
        Assert.AreEqual('', Src.SetAndGet(''),
            'DetailedMessage must round-trip an empty string');
    end;

    [Test]
    procedure DetailedMessage_Fresh_IsEmpty()
    begin
        // Positive: a default-initialised ErrorInfo has empty DetailedMessage.
        Assert.AreEqual('', Src.FreshDetailedMessage(),
            'Fresh ErrorInfo must have empty DetailedMessage');
    end;

    [Test]
    procedure DetailedMessage_LastWriteWins()
    begin
        // Proving: repeated setter calls overwrite — the last value wins.
        Assert.AreEqual(
            'second',
            Src.SetDifferentValues('first', 'second'),
            'DetailedMessage must reflect the last setter call');
    end;

    [Test]
    procedure DetailedMessage_Unicode()
    begin
        // Special characters round-trip through the storage.
        Assert.AreEqual(
            'caf\u00e9 & snowman \u2603',
            Src.SetAndGet('caf\u00e9 & snowman \u2603'),
            'DetailedMessage must preserve unicode characters');
    end;

    [Test]
    procedure DetailedMessage_DifferentInputs_DifferentOutputs_NegativeTrap()
    begin
        // Negative: guard against a stub that returns a fixed value.
        Assert.AreNotEqual(
            Src.SetAndGet('alpha'),
            Src.SetAndGet('beta'),
            'Different inputs must produce different DetailedMessage values');
    end;
}
