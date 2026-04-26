codeunit 97001 "HHM Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "HHM Src";

    // ── TryAddWithoutValidation ──────────────────────────────────

    [Test]
    procedure HttpHeaders_TryAddWithoutValidation_ReturnsTrue()
    begin
        Assert.IsTrue(Src.TryAddWithoutValidation('X-Custom', 'value'),
            'TryAddWithoutValidation must return true on success');
    end;

    [Test]
    procedure HttpHeaders_TryAddWithoutValidation_HeaderIsPresent()
    begin
        Assert.IsTrue(Src.TryAddThenContains('X-Custom', 'value'),
            'Header added via TryAddWithoutValidation must be retrievable via Contains');
    end;

    // ── Clear ────────────────────────────────────────────────────

    [Test]
    procedure HttpHeaders_Clear_RemovesAllHeaders()
    begin
        Assert.IsFalse(Src.ClearRemovesHeaders('X-Test', 'val'),
            'Contains must return false after Clear');
    end;

    // ── Keys ─────────────────────────────────────────────────────

    [Test]
    procedure HttpHeaders_Keys_ContainsAddedHeader()
    begin
        Assert.IsTrue(Src.KeysContainsAdded('X-Keys', 'v'),
            'Keys() must include names of headers that have been added');
    end;

    // ── ContainsSecret ───────────────────────────────────────────

    [Test]
    procedure HttpHeaders_ContainsSecret_FalseForPlainHeader()
    begin
        Assert.IsFalse(Src.ContainsSecretFalseForPlain('X-Plain', 'value'),
            'ContainsSecret must return false for plain (non-secret) headers');
    end;

    // ── GetSecretValues ──────────────────────────────────────────

    [Test]
    procedure HttpHeaders_GetSecretValues_ReturnsOneForPlainHeader()
    begin
        // The runner treats all headers as plain text; GetSecretValues wraps them
        // as SecretText and returns them — count must be 1 for a single header added.
        Assert.AreEqual(1, Src.GetSecretValuesCount('X-Plain', 'value'),
            'GetSecretValues must return 1 entry for a single plain header added');
    end;
}
