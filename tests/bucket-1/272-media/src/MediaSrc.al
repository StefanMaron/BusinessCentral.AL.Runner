/// Helper codeunit exercising Media methods.
codeunit 84407 "Media Src"
{
    // ── MediaId ─────────────────────────────────────────────────────────────────
    procedure GetMediaId(var m: Media): Guid
    begin
        exit(m.MediaId());
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
    begin
        exit(m.ImportFile(FileName));
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
    begin
        exit(m.ExportFile(FileName));
    end;

    // ── ImportStream ─────────────────────────────────────────────────────────────
    procedure ImportStreamReturnsTrue(content: Text): Boolean
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
        exit(m.ImportStream(InStr, 'test.jpg'));
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
    begin
        Storage.Init();
        Storage.Data.CreateOutStream(OutStr);
        exit(m.ExportStream(OutStr));
    end;

    // ── FindOrphans ──────────────────────────────────────────────────────────────
    procedure FindOrphansReturnsEmptyList(): Integer
    var
        Guids: List of [Guid];
    begin
        Guids := Media.FindOrphans();
        exit(Guids.Count());
    end;
}
