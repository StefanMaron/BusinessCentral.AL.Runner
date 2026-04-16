codeunit 86001 "FU Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "FU Src";

    // -----------------------------------------------------------------------
    // Positive: FileUpload.FileName() on a default variable returns empty string.
    // This is the only constructable state in standalone mode — no upload dialog.
    // -----------------------------------------------------------------------

    [Test]
    procedure FileName_DefaultUpload_ReturnsEmpty()
    var
        Upload: FileUpload;
    begin
        // Positive: default FileUpload has no name — must return ''.
        Assert.AreEqual('', Src.GetFileName(Upload),
            'FileUpload.FileName() must return empty string for default instance');
    end;

    // -----------------------------------------------------------------------
    // Positive: FileUpload.CreateInStream() on a default upload yields an
    // empty stream (EOS immediately) — no data in a default upload.
    // -----------------------------------------------------------------------

    [Test]
    procedure CreateInStream_DefaultUpload_StreamIsAtEOS()
    var
        Upload: FileUpload;
    begin
        // Positive: default upload has no content — stream must be at EOS.
        Assert.IsTrue(Src.CreateStreamAndCheckEOS(Upload),
            'InStream.EOS() must be true immediately after CreateInStream on empty upload');
    end;

    // -----------------------------------------------------------------------
    // Positive: CreateInStream overload with TextEncoding also yields EOS.
    // -----------------------------------------------------------------------

    [Test]
    procedure CreateInStreamWithEncoding_DefaultUpload_StreamIsAtEOS()
    var
        Upload: FileUpload;
    begin
        // Positive: encoding overload of CreateInStream must also complete without error.
        Assert.IsTrue(Src.CreateStreamWithEncoding(Upload),
            'CreateInStream with TextEncoding must complete and stream must be at EOS');
    end;

    // -----------------------------------------------------------------------
    // Negative: FileName returns exactly '' — not a whitespace string or other default.
    // -----------------------------------------------------------------------

    [Test]
    procedure FileName_DefaultUpload_IsExactlyEmptyString()
    var
        Upload: FileUpload;
        Name: Text;
    begin
        // Negative: FileName must be exactly '' with length 0.
        Name := Src.GetFileName(Upload);
        Assert.AreEqual(0, StrLen(Name),
            'FileUpload.FileName() must have length 0 for default instance');
    end;
}
