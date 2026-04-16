/// Helper codeunit exercising Media methods.
codeunit 84407 "Media Src"
{
    // ── MediaId ─────────────────────────────────────────────────────────────────
    procedure GetMediaId(var m: Media): Guid
    var
        Id: Guid;
    begin
        // Assign to local variable first to avoid AL0282 (nullable return guard).
        Id := m.MediaId();
        exit(Id);
    end;

    // ── HasValue ─────────────────────────────────────────────────────────────────
    procedure MediaHasValue(var m: Media): Boolean
    begin
        exit(m.HasValue());
    end;

    // ── ImportFile ───────────────────────────────────────────────────────────────
    procedure ImportFileReturnsTrue(FileName: Text): Boolean
    var
        m: Media;
        Ok: Boolean;
    begin
        // Assign to local Boolean first to avoid AL0282 on nullable return.
        Ok := m.ImportFile(FileName);
        exit(Ok);
    end;

    // ── ImportFile sets HasValue ─────────────────────────────────────────────────
    procedure HasValueAfterImportFile(FileName: Text): Boolean
    var
        m: Media;
    begin
        m.ImportFile(FileName);
        exit(m.HasValue());
    end;

    // ── ExportFile ───────────────────────────────────────────────────────────────
    procedure ExportFileOnDefaultReturnsFalse(FileName: Text): Boolean
    var
        m: Media;
        Ok: Boolean;
    begin
        Ok := m.ExportFile(FileName);
        exit(Ok);
    end;

    // ── ImportStream ─────────────────────────────────────────────────────────────
    procedure ImportStreamReturnsTrue(content: Text): Boolean
    var
        m: Media;
        Storage: Record "Media Test Storage" temporary;
        OutStr: OutStream;
        InStr: InStream;
        Ok: Boolean;
    begin
        Storage.Init();
        Storage.Data.CreateOutStream(OutStr);
        OutStr.WriteText(content);
        Storage.Data.CreateInStream(InStr);
        Ok := m.ImportStream(InStr, 'test.jpg');
        exit(Ok);
    end;

    procedure HasValueAfterImportStream(content: Text): Boolean
    var
        m: Media;
        Storage: Record "Media Test Storage" temporary;
        OutStr: OutStream;
        InStr: InStream;
    begin
        Storage.Init();
        Storage.Data.CreateOutStream(OutStr);
        OutStr.WriteText(content);
        Storage.Data.CreateInStream(InStr);
        m.ImportStream(InStr, 'test.jpg');
        exit(m.HasValue());
    end;

    // ── ExportStream ─────────────────────────────────────────────────────────────
    procedure ExportStreamOnDefaultReturnsFalse(): Boolean
    var
        m: Media;
        Storage: Record "Media Test Storage" temporary;
        OutStr: OutStream;
        Ok: Boolean;
    begin
        Storage.Init();
        Storage.Data.CreateOutStream(OutStr);
        Ok := m.ExportStream(OutStr);
        exit(Ok);
    end;

}
