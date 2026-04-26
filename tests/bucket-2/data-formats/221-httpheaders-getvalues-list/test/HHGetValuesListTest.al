codeunit 62211 "HH GetValues List Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "HH GetValues List Src";

    // ── Positive: value is returned ──────────────────────────────

    [Test]
    procedure GetValues_List_ReturnsAddedValue()
    begin
        // Positive: GetValues into List of [Text] must return the value added.
        Assert.AreEqual('application/json', Src.GetFirstValue('Content-Type', 'application/json'),
            'GetValues into List of [Text] must return the added header value');
    end;

    [Test]
    procedure GetValues_List_CountIsOne()
    begin
        // Positive: one value added → count in list must be 1.
        Assert.AreEqual(1, Src.GetValuesCount('X-Custom', 'hello'),
            'GetValues must populate the list with exactly one value');
    end;

    [Test]
    procedure GetValues_List_NotEmpty()
    begin
        // Positive: value must be non-empty (guards against a no-op stub).
        Assert.AreNotEqual('', Src.GetFirstValue('Accept', 'text/plain'),
            'GetValues must return a non-empty value for a present header');
    end;

    // ── Negative: missing key returns false ───────────────────────

    [Test]
    procedure GetValues_List_MissingKeyReturnsFalse()
    begin
        // Negative: GetValues for an absent key must return false.
        Assert.IsFalse(Src.GetValuesMissingKey('X-Missing'),
            'GetValues must return false when the header key is absent');
    end;
}
