codeunit 84408 "Media Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "Media Src";

    // ── MediaId ──────────────────────────────────────────────────────────────────
    [Test]
    procedure MediaId_Default_IsNotEmptyGuid()
    var
        EmptyGuid: Guid;
    begin
        // Positive: MediaId() must return a non-empty GUID.
        Assert.AreNotEqual(EmptyGuid, Src.GetMediaId(),
            'MediaId() must not equal the empty GUID');
    end;

    [Test]
    procedure MediaId_TwoInstances_DifferentIds()
    begin
        // Negative: two different Media instances must have different MediaIds.
        Assert.AreNotEqual(Src.GetMediaId(), Src.GetMediaId(),
            'Two Media instances must have different MediaIds');
    end;

    // ── HasValue ─────────────────────────────────────────────────────────────────
    [Test]
    procedure HasValue_Default_IsFalse()
    begin
        // Positive: a default (unimported) Media field must have HasValue = false.
        Assert.IsFalse(Src.MediaHasValueDefault(),
            'HasValue must be false for default (unimported) Media');
    end;

    // ── ImportFile ───────────────────────────────────────────────────────────────
    [Test]
    procedure ImportFile_ReturnsTrue()
    begin
        // Positive: ImportFile stub must return true.
        Assert.IsTrue(Src.ImportFileReturnsTrue('image.jpg'),
            'ImportFile must return true (in-memory stub)');
    end;

    [Test]
    procedure ImportFile_SetsHasValueTrue()
    begin
        // Positive: after ImportFile, HasValue must be true.
        Assert.IsTrue(Src.HasValueAfterImportFile('image.jpg'),
            'HasValue must be true after ImportFile');
    end;

    // ── ExportFile ───────────────────────────────────────────────────────────────
    [Test]
    procedure ExportFile_DefaultMedia_ReturnsFalse()
    begin
        // Positive (stub): ExportFile on a default Media returns false (no data).
        Assert.IsFalse(Src.ExportFileOnDefaultReturnsFalse('output.jpg'),
            'ExportFile on default Media must return false (no data to export)');
    end;

    [Test]
    procedure ExportFile_DefaultMedia_DoesNotEqualTrue()
    begin
        // Negative: ExportFile result must not be true for default (empty) Media.
        Assert.AreNotEqual(true, Src.ExportFileOnDefaultReturnsFalse('output.jpg'),
            'ExportFile on empty Media must not return true');
    end;

    // ── ImportStream ─────────────────────────────────────────────────────────────
    [Test]
    procedure ImportStream_ReturnsTrue()
    begin
        // Positive: ImportStream stub must return true.
        Assert.IsTrue(Src.ImportStreamReturnsTrue('image data'),
            'ImportStream must return true (in-memory stub)');
    end;

    [Test]
    procedure ImportStream_SetsHasValueTrue()
    begin
        // Positive: after ImportStream, HasValue must be true.
        Assert.IsTrue(Src.HasValueAfterImportStream('image data'),
            'HasValue must be true after ImportStream');
    end;

    // ── ExportStream ─────────────────────────────────────────────────────────────
    [Test]
    procedure ExportStream_DefaultMedia_ReturnsFalse()
    begin
        // Positive (stub): ExportStream on a default Media returns false (no data).
        Assert.IsFalse(Src.ExportStreamOnDefaultReturnsFalse(),
            'ExportStream on default Media must return false (no data to export)');
    end;

}
