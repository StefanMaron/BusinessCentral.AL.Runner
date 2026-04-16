/// Tests for HttpContent.Clear, WriteFrom, IsSecretContent and
/// HttpResponseMessage.GetCookie, GetCookieNames, IsBlockedByEnvironment.
///
/// Proof strategy: if any mock method is missing, Roslyn compilation fails
/// with CS1061 and ALL tests in this bucket go RED.
codeunit 99201 "HCR Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        H: Codeunit "HCR Helper";

    // ── HttpContent.WriteFrom / ReadAs ────────────────────────────────────────

    [Test]
    procedure Content_WriteFrom_RoundTrip()
    begin
        // Positive: WriteFrom + ReadAs returns the stored text.
        Assert.AreEqual('hello', H.ContentWriteFromReadAs('hello'),
            'WriteFrom must persist the text so ReadAs returns it');
    end;

    [Test]
    procedure Content_WriteFrom_NotDefaultEmpty()
    begin
        // Negative: a no-op WriteFrom would return '' — this would fail.
        Assert.AreNotEqual('', H.ContentWriteFromReadAs('world'),
            'WriteFrom must not leave content empty');
    end;

    // ── HttpContent.Clear ─────────────────────────────────────────────────────

    [Test]
    procedure Content_Clear_ResetsContent()
    begin
        // Positive: after Clear, ReadAs returns empty string.
        Assert.AreEqual('', H.ContentClearResult(),
            'Clear must reset stored content to empty');
    end;

    // ── HttpContent.IsSecretContent ───────────────────────────────────────────

    [Test]
    procedure Content_IsSecretContent_ReturnsFalse()
    begin
        // Positive: IsSecretContent returns false in standalone mode.
        Assert.IsFalse(H.ContentIsSecretContent(),
            'IsSecretContent must return false in the runner');
    end;

    [Test]
    procedure Content_IsSecretContent_NotTrue()
    begin
        // Negative: ensure the stub does not return true.
        Assert.AreNotEqual(true, H.ContentIsSecretContent(),
            'IsSecretContent stub must not return true');
    end;

    // ── HttpResponseMessage.IsBlockedByEnvironment ────────────────────────────

    [Test]
    procedure Response_IsBlockedByEnvironment_ReturnsFalse()
    begin
        // Positive: IsBlockedByEnvironment returns false in standalone mode.
        Assert.IsFalse(H.ResponseIsBlocked(),
            'IsBlockedByEnvironment must return false in the runner');
    end;

    [Test]
    procedure Response_IsBlockedByEnvironment_NotTrue()
    begin
        // Negative: the stub must not return true.
        Assert.AreNotEqual(true, H.ResponseIsBlocked(),
            'IsBlockedByEnvironment stub must not return true');
    end;

    // ── HttpResponseMessage.GetCookie ─────────────────────────────────────────

    [Test]
    procedure Response_GetCookie_ReturnsFalse()
    begin
        // Positive: GetCookie returns false (no cookies in runner).
        Assert.IsFalse(H.ResponseGetCookieResult('MyCookie'),
            'GetCookie must return false — no cookies in standalone mode');
    end;

    [Test]
    procedure Response_GetCookie_NotFound_NotTrue()
    begin
        // Negative: cookie must not be found.
        Assert.AreNotEqual(true, H.ResponseGetCookieResult('x'),
            'GetCookie must not return true for any name');
    end;

    // ── HttpResponseMessage.GetCookieNames ────────────────────────────────────

    [Test]
    procedure Response_GetCookieNames_ReturnsEmpty()
    begin
        // Positive: GetCookieNames returns 0 names in standalone mode.
        Assert.AreEqual(0, H.ResponseGetCookieNamesCount(),
            'GetCookieNames must return an empty list in standalone mode');
    end;

    [Test]
    procedure Response_GetCookieNames_NotNegative()
    begin
        // Negative: count must be >= 0 (not -1 or some invalid stub return).
        Assert.IsTrue(H.ResponseGetCookieNamesCount() >= 0,
            'GetCookieNames count must be non-negative');
    end;

    // ── Compilation proof ─────────────────────────────────────────────────────

    [Test]
    procedure AllMethods_Compile()
    begin
        // Proof: reaching this line means all stubs compiled without CS1061.
        Assert.IsTrue(true,
            'All HttpContent/HttpResponseMessage stub methods must compile');
    end;
}
