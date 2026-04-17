codeunit 84408 "Media Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "Media Src";

    // ── HasValue ─────────────────────────────────────────────────────────────────
    [Test]
    procedure HasValue_Default_IsFalse()
    begin
        Assert.IsFalse(Src.MediaHasValueDefault(),
            'HasValue must be false for default (unimported) Media');
    end;

    // ── ImportFile ───────────────────────────────────────────────────────────────
    [Test]
    procedure ImportFile_ReturnsTrue()
    begin
        Assert.IsTrue(Src.ImportFileReturnsTrue('image.jpg'),
            'ImportFile must return true (in-memory stub)');
    end;

    [Test]
    procedure ImportFile_SetsHasValueTrue()
    begin
        Assert.IsTrue(Src.HasValueAfterImportFile('image.jpg'),
            'HasValue must be true after ImportFile');
    end;

    // ── ExportFile ───────────────────────────────────────────────────────────────
    [Test]
    procedure ExportFile_DefaultMedia_ReturnsFalse()
    begin
        Assert.IsFalse(Src.ExportFileOnDefaultReturnsFalse('output.jpg'),
            'ExportFile on default Media must return false (no data to export)');
    end;

    [Test]
    procedure ExportFile_DefaultMedia_DoesNotEqualTrue()
    begin
        Assert.AreNotEqual(true, Src.ExportFileOnDefaultReturnsFalse('output.jpg'),
            'ExportFile on empty Media must not return true');
    end;

}
