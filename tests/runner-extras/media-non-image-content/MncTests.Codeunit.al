/// <summary>
/// A Media field must accept content that is not an image.
///
/// BC classifies media by trying to decode it as an image and, when that fails, storing it
/// as application/octet-stream — that fallback is the only reason a report layout, a PDF or
/// any other blob can live in a Media field at all. It keys off the decode failing with an
/// ArgumentException; this platform has no System.Drawing, so the failure arrived as a
/// different type, the fallback never ran, and every media write failed with "the media
/// object could not be loaded because it is not a valid image type" — including writes that
/// were never images.
///
/// Both directions:
///   * non-image content  -> stores, and reads back byte-for-byte;
///   * image content      -> refused BY NAME (this platform cannot decode an image), never
///     stored as something the caller would then read back wrong.
/// </summary>
codeunit 62042 "MNC Tests"
{
    Subtype = Test;

    var
        TemplateTok: Label '<pageworks><text>not an image</text></pageworks>', Locked = true;

    local procedure ImportText(var Asset: Record "MNC Asset"; Content: Text)
    var
        TempBlob: Codeunit "Temp Blob";
        ContentOutStream: OutStream;
        ContentInStream: InStream;
    begin
        TempBlob.CreateOutStream(ContentOutStream);
        ContentOutStream.WriteText(Content);
        TempBlob.CreateInStream(ContentInStream);
        Asset.Content.ImportStream(ContentInStream, 'MNC content');
    end;

    [Test]
    procedure NonImageContent_StoresAndReadsBack()
    var
        Asset: Record "MNC Asset";
        TempBlob: Codeunit "Temp Blob";
        ExportOutStream: OutStream;
        ExportInStream: InStream;
        RoundTripped: Text;
    begin
        Asset.DeleteAll();
        Asset.Init();
        Asset."No." := 'DOC-1';
        Asset.Insert();

        ImportText(Asset, TemplateTok);
        Asset.Modify();

        if not Asset.Content.HasValue() then
            Error('The Media field reports no value after importing non-image content.');

        // Reading it back is the real claim: "the write did not throw" would still hold if
        // the content had been dropped.
        Asset.Get('DOC-1');
        TempBlob.CreateOutStream(ExportOutStream);
        Asset.Content.ExportStream(ExportOutStream);
        TempBlob.CreateInStream(ExportInStream);
        ExportInStream.ReadText(RoundTripped);
        if RoundTripped <> TemplateTok then
            Error('Media content did not round-trip. Expected <%1>, got <%2>.', TemplateTok, RoundTripped);
    end;

    [Test]
    procedure ImageContent_IsRefusedByName()
    var
        Asset: Record "MNC Asset";
        TempBlob: Codeunit "Temp Blob";
        Base64Convert: Codeunit "Base64 Convert";
        ContentOutStream: OutStream;
        ContentInStream: InStream;
    begin
        Asset.DeleteAll();
        Asset.Init();
        Asset."No." := 'IMG-1';
        Asset.Insert();

        // 'iVBORw0KGgo=' decodes to the 8-byte PNG signature 89 50 4E 47 0D 0A 1A 0A. Written
        // through Base64Convert because AL's OutStream.Write is typed — it cannot emit single
        // raw bytes. Real image content, so the runner must say plainly that it cannot decode
        // one here rather than storing it as a blob a caller would read back as an image that
        // never decoded.
        TempBlob.CreateOutStream(ContentOutStream);
        Base64Convert.FromBase64('iVBORw0KGgo=', ContentOutStream);
        TempBlob.CreateInStream(ContentInStream);

        asserterror Asset.Content.ImportStream(ContentInStream, 'MNC image');
        if StrPos(GetLastErrorText(), 'media-image-decode') = 0 then
            Error('Expected a named media-image-decode refusal, got: %1', GetLastErrorText());
    end;
}
