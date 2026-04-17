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

    // ── ImportStream ─────────────────────────────────────────────────────────────
    procedure ImportStreamReturnsTrue(content: Text): Boolean
    var
        Storage: Record "Media Test Storage" temporary;
        OutStr: OutStream;
        InStr: InStream;
        Ok: Boolean;
    begin
        Storage.Init();
        Storage.Data.CreateOutStream(OutStr);
        OutStr.WriteText(content);
        Storage.Data.CreateInStream(InStr);
        Ok := Storage.MediaField.ImportStream(InStr, 'test.jpg');
        exit(Ok);
    end;

    procedure HasValueAfterImportStream(content: Text): Boolean
    var
        Storage: Record "Media Test Storage" temporary;
        OutStr: OutStream;
        InStr: InStream;
        HasVal: Boolean;
    begin
        Storage.Init();
        Storage.Data.CreateOutStream(OutStr);
        OutStr.WriteText(content);
        Storage.Data.CreateInStream(InStr);
        Storage.MediaField.ImportStream(InStr, 'test.jpg');
        HasVal := Storage.MediaField.HasValue();
        exit(HasVal);
    end;

    // ── ExportStream ─────────────────────────────────────────────────────────────
    procedure ExportStreamOnDefaultReturnsFalse(): Boolean
    var
        Storage: Record "Media Test Storage" temporary;
        OutStr: OutStream;
        Ok: Boolean;
    begin
        Storage.Init();
        Storage.Data.CreateOutStream(OutStr);
        Ok := Storage.MediaField.ExportStream(OutStr);
        exit(Ok);
    end;

}
