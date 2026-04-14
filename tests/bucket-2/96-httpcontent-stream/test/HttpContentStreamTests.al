codeunit 56981 "HTTP Content Stream Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Probe: Codeunit "HTTP Content Stream Probe";

    /// <summary>
    /// Positive: WriteBodyFromText compiles and returns the body text unchanged.
    /// This tests the text-based HttpContent.WriteFrom path (ALLoadFrom(NavText))
    /// which already worked before the fix — regression guard.
    /// </summary>
    [Test]
    procedure TestWriteBodyFromText_ReturnsBody()
    var
        Result: Text;
    begin
        Result := Probe.WriteBodyFromText('{"key":"value"}');
        Assert.AreEqual('{"key":"value"}', Result, 'WriteBodyFromText must return the body text');
    end;

    /// <summary>
    /// Negative: WriteBodyFromText with empty string returns empty.
    /// </summary>
    [Test]
    procedure TestWriteBodyFromText_Empty()
    var
        Result: Text;
    begin
        Result := Probe.WriteBodyFromText('');
        Assert.AreEqual('', Result, 'Empty body should return empty string');
    end;

    /// <summary>
    /// Positive: GetHeaderValue compiles and returns the header value.
    /// Tests that HttpRequest.GetHeaders + HttpHeaders.Add compiles correctly.
    /// </summary>
    [Test]
    procedure TestGetHeaderValue_ReturnsValue()
    var
        Result: Text;
    begin
        Result := Probe.GetHeaderValue('Content-Type', 'application/json');
        Assert.AreEqual('application/json', Result, 'GetHeaderValue must return the header value');
    end;

    /// <summary>
    /// Positive: The codeunit that declares WriteBodyFromStream compiled successfully
    /// (no CS1503 for ALLoadFrom(MockInStream)). Calling it would require HTTP service
    /// tier so we only verify the method exists via the compile-time proof.
    /// We call WriteBodyFromText as the runtime stand-in to confirm the whole codeunit
    /// loaded into the assembly.
    /// </summary>
    [Test]
    procedure TestHttpContentStreamCodunitLoaded()
    var
        Result: Text;
    begin
        // If the codeunit compiled successfully (the fix worked), this call succeeds.
        // WriteBodyFromStream/ReadBodyIntoStream are proof-of-compilation methods only.
        Result := Probe.WriteBodyFromText('stream-test');
        Assert.AreEqual('stream-test', Result, 'Codeunit must be loaded (stream methods compiled OK)');
    end;

    /// <summary>
    /// Negative: WriteBodyFromText with non-empty content that contains special chars.
    /// Guards against accidental truncation or encoding issues in ALLoadFrom.
    /// </summary>
    [Test]
    procedure TestWriteBodyFromText_SpecialChars()
    var
        Body: Text;
        Result: Text;
    begin
        Body := '{"msg":"hello\nworld","val":42}';
        Result := Probe.WriteBodyFromText(Body);
        Assert.AreEqual(Body, Result, 'Body with special chars must round-trip unchanged');
    end;
}
