codeunit 50143 "BLEI Blob Export Import Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "BLEI Helper";

    // ── Stream round-trip ──────────────────────────────────────────

    [Test]
    procedure BlobStreamRoundTrip_PreservesText()
    begin
        // Positive: CreateOutStream write + CreateInStream read round-trip
        // returns the exact text that was written.
        Assert.AreEqual('hello world', Helper.WriteAndRead('hello world'),
            'Blob round-trip must preserve text exactly');
    end;

    [Test]
    procedure BlobStreamRoundTrip_EmptyString()
    begin
        // Positive: empty string round-trips correctly.
        Assert.AreEqual('', Helper.WriteAndRead(''),
            'Empty string round-trip must return empty');
    end;

    [Test]
    procedure BlobStreamRoundTrip_NotWrongValue()
    begin
        // Negative: the read-back value must not be a different string.
        Assert.AreNotEqual('wrong', Helper.WriteAndRead('correct'),
            'Read-back must not return wrong value');
    end;

    // ── HasValue ──────────────────────────────────────────────────

    [Test]
    procedure Blob_HasValueAfterWrite_True()
    begin
        // Positive: HasValue returns true after writing data.
        Assert.IsTrue(Helper.HasValueAfterWrite(), 'HasValue must be true after write');
    end;

    // ── Length ────────────────────────────────────────────────────

    [Test]
    procedure Blob_LengthAfterWrite_Positive()
    begin
        // Positive: Length is positive after writing a non-empty string.
        Assert.IsTrue(Helper.LengthAfterWrite('hello') > 0,
            'Blob length must be positive after write');
    end;

    [Test]
    procedure Blob_LengthAfterWrite_Zero_ForEmpty()
    begin
        // Positive: Length is 0 for an empty blob (no write).
        Assert.AreEqual(0, Helper.LengthAfterWrite(''),
            'Blob length must be 0 when nothing was written');
    end;

    // ── Export / Import stubs ─────────────────────────────────────

    [Test]
    procedure Blob_Export_DoesNotThrow()
    begin
        // Positive: Blob.Export compiles and does not throw an unhandled error.
        // The runner stubs Export as a no-op (file I/O is out of scope).
        // The test verifies the stub is present and callable.
        Helper.TryExport(); // must not raise unhandled error
        Assert.IsTrue(true, 'Blob.Export must not throw');
    end;

    [Test]
    procedure Blob_Import_DoesNotThrow()
    begin
        // Positive: Blob.Import compiles and does not throw an unhandled error.
        // The runner stubs Import as a no-op (file I/O is out of scope).
        Helper.TryImport(); // must not raise unhandled error
        Assert.IsTrue(true, 'Blob.Import must not throw');
    end;
}
