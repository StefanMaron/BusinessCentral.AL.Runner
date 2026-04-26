codeunit 59611 "SWST Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    /// Positive: OutStream.Write(Text) + InStream.Read(Text) must round-trip a non-empty string.
    [Test]
    procedure WriteAndRead_RoundTrips_Text()
    var
        Src: Codeunit "SWST Src";
    begin
        Assert.AreEqual('hello world', Src.WriteAndRead('hello world'),
            'OutStream.Write then InStream.Read must round-trip the text value');
    end;

    /// Positive: fixed-length Text[50] must round-trip without truncation.
    [Test]
    procedure WriteAndRead_RoundTrips_FixedText()
    var
        Src: Codeunit "SWST Src";
    begin
        Assert.AreEqual('NavAL test', Src.WriteAndReadFixed('NavAL test'),
            'OutStream.Write(Text[50]) then InStream.Read must round-trip');
    end;

    /// Positive: empty string must round-trip as empty (no crash, no extra bytes).
    [Test]
    procedure WriteAndRead_EmptyString_RoundTrips()
    var
        Src: Codeunit "SWST Src";
    begin
        Assert.AreEqual('', Src.WriteAndRead(''),
            'Empty string must round-trip through OutStream.Write/InStream.Read');
    end;

    /// Negative: different inputs must produce different round-trip outputs —
    /// guards against a no-op stub that always returns a fixed value.
    [Test]
    procedure WriteAndRead_DifferentInputs_ProduceDifferentOutputs()
    var
        Src: Codeunit "SWST Src";
    begin
        Assert.AreNotEqual(
            Src.WriteAndRead('alpha'),
            Src.WriteAndRead('beta'),
            'Different input must produce different round-trip output');
    end;
}
