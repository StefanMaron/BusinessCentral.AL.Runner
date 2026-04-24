codeunit 304011 "InStream To String Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure InStreamToString_Compiles()
    var
        Src: Codeunit "InStream To String Src";
    begin
        // Positive: codeunit compiled with InStream operations.
        // Sentinel returns 1273 (non-default), proving the codeunit is live.
        Assert.AreEqual(1273, Src.GetVersion(),
            'Codeunit must compile and return 1273 from GetVersion()');
    end;

    [Test]
    procedure InStreamToString_RoundTrip()
    var
        Src: Codeunit "InStream To String Src";
    begin
        // Positive: write text to Blob, read back via InStream — proves
        // InStream content is correctly read as text.
        Assert.AreEqual('Hello World', Src.WriteAndReadBlob('Hello World'),
            'InStream round-trip must return the original text');
    end;

    [Test]
    procedure InStreamToString_EmptyBlob()
    var
        Src: Codeunit "InStream To String Src";
    begin
        // Positive: empty input round-trips as empty string.
        Assert.AreEqual('', Src.WriteAndReadBlob(''),
            'Empty text round-trip must return empty string');
    end;
}
