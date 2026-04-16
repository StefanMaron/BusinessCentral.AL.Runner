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
        m: Media;
        EmptyGuid: Guid;
    begin
        // Positive: MediaId() must return a non-empty GUID.
        Assert.AreNotEqual(EmptyGuid, Src.GetMediaId(m),
            'MediaId() must not equal the empty GUID');
    end;

    [Test]
    procedure MediaId_TwoInstances_DifferentIds()
    var
        m1: Media;
        m2: Media;
    begin
        // Negative: two different Media instances must have different MediaIds.
        Assert.AreNotEqual(Src.GetMediaId(m1), Src.GetMediaId(m2),
            'Two Media instances must have different MediaIds');
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

    // ── FindOrphans ───────────────────────────────────────────────────────────────
    [Test]
    procedure FindOrphans_ReturnsEmptyList()
    begin
        // Positive: FindOrphans stub returns an empty list.
        Assert.AreEqual(0, Src.FindOrphansReturnsEmptyList(),
            'FindOrphans must return an empty list (no orphans in test environment)');
    end;

    [Test]
    procedure FindOrphans_CountIsZero()
    begin
        // Negative: FindOrphans count must not be > 0 in test environment.
        Assert.IsFalse(Src.FindOrphansReturnsEmptyList() > 0,
            'FindOrphans must not return any orphan GUIDs in test environment');
    end;
}
