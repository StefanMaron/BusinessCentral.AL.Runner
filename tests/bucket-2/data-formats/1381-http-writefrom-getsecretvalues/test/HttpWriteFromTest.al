/// Tests for HttpContent.WriteFrom(Text), WriteFrom(SecretText) round-trips
/// and HttpHeaders.GetSecretValues(Text, List of [SecretText]) — issue #1381.
codeunit 310301 "HWF Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "HWF Src";

    // ── HttpContent.WriteFrom(Text) ──────────────────────────────────────────

    [Test]
    procedure WriteFromText_RoundTrip()
    begin
        // Positive: WriteFrom(Text) stores the text; ReadAs returns it.
        Assert.AreEqual('hello-world', Src.WriteFromTextRoundTrip('hello-world'),
            'WriteFrom(Text) must persist the body so ReadAs returns it');
    end;

    [Test]
    procedure WriteFromText_NotEmpty()
    begin
        // Positive (anti-stub): a no-op WriteFrom would return '' — this proves it stores the value.
        Assert.AreNotEqual('', Src.WriteFromTextRoundTrip('non-empty-body'),
            'WriteFrom(Text) must not leave content empty');
    end;

    // ── HttpContent.WriteFrom(SecretText) ────────────────────────────────────

    [Test]
    procedure WriteFromSecret_RoundTrip()
    begin
        // Positive: WriteFrom(SecretText) stores the plain-text value; ReadAs returns it.
        Assert.AreEqual('secret-payload', Src.WriteFromSecretRoundTrip('secret-payload'),
            'WriteFrom(SecretText) must persist the body (as plain text) so ReadAs returns it');
    end;

    [Test]
    procedure WriteFromSecret_NotEmpty()
    begin
        // Positive (anti-stub): proves a non-empty secret body is stored.
        Assert.AreNotEqual('', Src.WriteFromSecretRoundTrip('my-secret'),
            'WriteFrom(SecretText) must not leave content empty');
    end;

    // ── HttpHeaders.GetSecretValues(Text, List of [SecretText]) ──────────────

    [Test]
    procedure GetSecretValues_List_CountIsOne()
    begin
        // Positive: one header added → GetSecretValues list must have 1 entry.
        Assert.AreEqual(1, Src.GetSecretValuesCount('Authorization', 'bearer-token-xyz'),
            'GetSecretValues must populate the list with exactly one entry');
    end;

    [Test]
    procedure GetSecretValues_List_ReturnsValue()
    begin
        // Positive: the retrieved SecretText must unwrap to the original header value.
        Assert.AreEqual('my-api-key-123', Src.GetSecretValuesFirst('X-Api-Key', 'my-api-key-123'),
            'GetSecretValues must return the stored header value as a SecretText');
    end;

    [Test]
    procedure GetSecretValues_List_NotEmpty()
    begin
        // Positive (anti-stub): guards against a no-op stub that always returns ''.
        Assert.AreNotEqual('', Src.GetSecretValuesFirst('X-Token', 'token-abc'),
            'GetSecretValues must return a non-empty value for a present header');
    end;

    [Test]
    procedure GetSecretValues_List_MissingKeyReturnsFalse()
    begin
        // Negative: GetSecretValues for a missing key must return false.
        Assert.IsFalse(Src.GetSecretValuesMissingKey('X-Missing'),
            'GetSecretValues must return false when the header key is absent');
    end;
}
