// Test suite for issue #1528 — CS0029 string→NavText rewriter fix.
//
// The RoslynRewriter was replacing user-defined ToText() calls with
// AlCompat.Format(receiver) which returns C# string. When BC scope classes
// call _parent.ToText(this) (passing the scope as first arg), the rewriter
// treated it as a BC runtime session call and wrongly replaced it.
// After the fix, bare-`this` first-arg calls are excluded from the replacement.
codeunit 1318002 "NavText Rewrite Tests"
{
    Subtype = Test;

    var Assert: Codeunit Assert;

    // Positive: user-defined ToText() called via GetTextViaToText() must
    // return the exact string — proves the rewriter did NOT replace it with
    // AlCompat.Format (which would return the codeunit's object representation,
    // not 'hello-from-totext').
    [Test]
    procedure UserToText_ReturnsDeclaredValue()
    var
        Src: Codeunit "NavText Rewrite Source";
    begin
        Assert.AreEqual('hello-from-totext', Src.GetTextViaToText(),
            'GetTextViaToText must return the value from the user ToText() procedure');
    end;

    // Negative: direct call to ToText() also returns the declared value.
    [Test]
    procedure UserToText_DirectCall_ReturnsDeclaredValue()
    var
        Src: Codeunit "NavText Rewrite Source";
    begin
        Assert.AreEqual('hello-from-totext', Src.ToText(),
            'Direct call to ToText() must return declared value');
    end;

    // Positive: TenantId() stub returns "STANDALONE" — non-default value
    // proves a no-op implementation returning '' would fail this assertion.
    [Test]
    procedure TenantId_ReturnsStandaloneString()
    var
        Src: Codeunit "NavText Rewrite Source";
    begin
        Assert.AreEqual('STANDALONE', Src.GetTenantId(),
            'TenantId() stub should return STANDALONE');
    end;

    // Positive: SerialNumber() stub returns "STANDALONE".
    [Test]
    procedure SerialNumber_ReturnsStandaloneString()
    var
        Src: Codeunit "NavText Rewrite Source";
    begin
        Assert.AreEqual('STANDALONE', Src.GetSerialNumber(),
            'SerialNumber() stub should return STANDALONE');
    end;

    // Positive: Callstack() stub returns empty — no crash.
    [Test]
    procedure CallStack_ReturnsEmpty()
    var
        Src: Codeunit "NavText Rewrite Source";
    begin
        Assert.AreEqual('', Src.GetCallStack(),
            'CallStack stub should return empty string');
    end;

    // Positive: ApplicationIdentifier() stub returns empty.
    [Test]
    procedure ApplicationIdentifier_ReturnsEmpty()
    var
        Src: Codeunit "NavText Rewrite Source";
    begin
        Assert.AreEqual('', Src.GetApplicationIdentifier(),
            'ApplicationIdentifier stub should return empty string');
    end;
}
