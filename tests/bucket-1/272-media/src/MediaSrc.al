/// Helper codeunit exercising Media methods via table field access.
codeunit 84407 "Media Src"
{
    // ── HasValue ─────────────────────────────────────────────────────────────────
    procedure MediaHasValueDefault(): Boolean
    var
        Storage: Record "Media Test Storage" temporary;
        HasVal: Boolean;
    begin
        HasVal := Storage.MediaField.HasValue();
        exit(HasVal);
    end;

    // ── ImportFile ───────────────────────────────────────────────────────────────
    procedure ImportFileReturnsTrue(FileName: Text): Boolean
    var
        Storage: Record "Media Test Storage" temporary;
        Ok: Boolean;
    begin
        Ok := Storage.MediaField.ImportFile(FileName, '');
        exit(Ok);
    end;

    // ── ImportFile sets HasValue ─────────────────────────────────────────────────
    procedure HasValueAfterImportFile(FileName: Text): Boolean
    var
        Storage: Record "Media Test Storage" temporary;
        HasVal: Boolean;
    begin
        Storage.MediaField.ImportFile(FileName, '');
        HasVal := Storage.MediaField.HasValue();
        exit(HasVal);
    end;

    // ── ExportFile ───────────────────────────────────────────────────────────────
    procedure ExportFileOnDefaultReturnsFalse(FileName: Text): Boolean
    var
        Storage: Record "Media Test Storage" temporary;
        Ok: Boolean;
    begin
        Ok := Storage.MediaField.ExportFile(FileName);
        exit(Ok);
    end;

}
