codeunit 84408 "Media Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "Media Src";

    // ── HasValue ──────────────────────────────────────────────────────────────

    [Test]
    procedure HasValue_DefaultRecord_IsFalse()
    var
        Rec: Record "Media Test Storage";
    begin
        // Negative: a fresh record has no media — HasValue must be false.
        Rec.Init();
        Rec.Id := 1;
        Rec.Insert();
        Assert.IsFalse(Src.GetHasValue(Rec), 'HasValue must be false for an uninitialised Media field');
    end;

    // ── ImportFile ────────────────────────────────────────────────────────────

    [Test]
    procedure ImportFile_ReturnsNonNullGuid()
    var
        Rec: Record "Media Test Storage";
        Result: Guid;
    begin
        // Positive: ImportFile returns a non-null GUID (stub always accepts the import).
        Rec.Init();
        Rec.Id := 2;
        Rec.Insert();
        Result := Src.ImportFileOnRec(Rec, 'photo.jpg', 'My Photo');
        Assert.IsFalse(IsNullGuid(Result), 'ImportFile must return a non-null GUID');
    end;

    [Test]
    procedure ImportFile_SetsHasValueTrue()
    var
        Rec: Record "Media Test Storage";
    begin
        // Positive: HasValue becomes true after ImportFile.
        Rec.Init();
        Rec.Id := 3;
        Rec.Insert();
        Src.ImportFileOnRec(Rec, 'photo.jpg', 'My Photo');
        Assert.IsTrue(Src.GetHasValue(Rec), 'HasValue must be true after ImportFile');
    end;

    // ── ExportFile ────────────────────────────────────────────────────────────

    [Test]
    procedure ExportFile_ReturnsFalse_WhenNoData()
    var
        Rec: Record "Media Test Storage";
        Result: Boolean;
    begin
        // Negative: ExportFile returns false — no blob data in standalone runner.
        Rec.Init();
        Rec.Id := 4;
        Rec.Insert();
        Result := Src.ExportFileFromRec(Rec, 'out.jpg');
        Assert.IsFalse(Result, 'ExportFile must return false when no media data is stored');
    end;

}
