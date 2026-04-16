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
    procedure HttpHeaders_GetSecretValues_EmptyForPlainHeader()
    begin
        Assert.AreEqual(0, Src.GetSecretValuesEmptyForPlain('X-Plain', 'value'),
            'GetSecretValues must return empty list for plain (non-secret) headers');
    end;
}
