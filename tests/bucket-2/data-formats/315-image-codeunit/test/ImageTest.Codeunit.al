/// Tests for Codeunit "Image" (3971) mock — issue #1421.
/// Verifies that GetWidth/GetHeight return real dimensions from a parsed image header,
/// not the default 0 that the auto-stub returns.
codeunit 315100 "IMG Image Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        // Minimal 1×1 PNG (hard-coded valid PNG bytes, base64-encoded).
        // PNG magic + IHDR chunk: width=1, height=1.
        Png1x1Lbl: Label 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGNgYAAAAAMAASsJTYQAAAAASUVORK5CYII=', Locked = true;

    [Test]
    procedure ImageFromBase64_GetWidthHeight_NonZeroForValidPng()
    var
        ImageSys: Codeunit Image;
    begin
        // Positive: a valid 1×1 PNG loaded via FromBase64 must report real dimensions.
        ImageSys.FromBase64(Png1x1Lbl);

        Assert.AreEqual(1, ImageSys.GetWidth(), 'GetWidth on a 1x1 PNG should be 1');
        Assert.AreEqual(1, ImageSys.GetHeight(), 'GetHeight on a 1x1 PNG should be 1');
    end;

    [Test]
    procedure ImageResize_UpdatesDimensions()
    var
        ImageSys: Codeunit Image;
    begin
        // Positive: after loading a 1×1 PNG and resizing to 4×8, GetWidth/Height reflect new size.
        ImageSys.FromBase64(Png1x1Lbl);
        ImageSys.Resize(4, 8);

        Assert.AreEqual(4, ImageSys.GetWidth(), 'GetWidth after Resize(4,8) should be 4');
        Assert.AreEqual(8, ImageSys.GetHeight(), 'GetHeight after Resize(4,8) should be 8');
    end;

    [Test]
    procedure ImageFromBase64_EmptyString_Errors()
    var
        ImageSys: Codeunit Image;
    begin
        // Negative: empty base64 string is not a valid image and must throw.
        asserterror ImageSys.FromBase64('');
        Assert.ExpectedError('image');
    end;

    [Test]
    procedure ImageFromBase64_InvalidBase64_Errors()
    var
        ImageSys: Codeunit Image;
    begin
        // Negative: a base64 string that decodes to non-image bytes must throw.
        asserterror ImageSys.FromBase64('aGVsbG8=');  // decodes to "hello"
        Assert.ExpectedError('image');
    end;
}
