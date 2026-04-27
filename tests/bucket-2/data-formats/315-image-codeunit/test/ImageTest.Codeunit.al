/// Regression tests for Codeunit "Image" (3971) blank-shell behaviour — issue #1502.
///
/// The runner reverted its real MockImage implementation (PNG/JPEG/GIF/BMP header
/// parsing) because shipping a real SA business-logic reimplementation violates the
/// runner's scope policy (see docs/limitations.md — "System Application codeunits
/// — scope policy"). The real MockImage C# class was deleted; codeunit 3971 is now
/// the auto-generated blank shell from Image.al.
///
/// Every method now returns the type-default (0, "") and does not throw.
/// These tests verify:
///   1. Image methods return type-defaults — not real pixel dimensions.
///   2. No method throws on valid base64 input.
///   3. No method throws on empty or nonsense input (blank shell ignores all input).
///
/// If your AL under test needs real Image dimensions, provide your own stub in your
/// test project (see docs/limitations.md "Bring your own stub").
codeunit 315100 "IMG Image Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Png1x1Lbl: Label 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGNgYAAAAAMAASsJTYQAAAAASUVORK5CYII=', Locked = true;

    // ── Blank-shell: GetWidth/GetHeight return 0 ─────────────────────────────

    [Test]
    procedure GetWidth_AfterFromBase64_ReturnsZero()
    var
        ImageSys: Codeunit Image;
    begin
        // [GIVEN] A valid 1x1 PNG loaded via FromBase64
        // [WHEN]  GetWidth is called on the blank-shell stub
        // [THEN]  Returns 0 — the blank shell does not parse image headers
        ImageSys.FromBase64(Png1x1Lbl);
        Assert.AreEqual(0, ImageSys.GetWidth(),
            'Blank-shell Image.GetWidth() must return 0 (no header parsing in standalone mode)');
    end;

    [Test]
    procedure GetHeight_AfterFromBase64_ReturnsZero()
    var
        ImageSys: Codeunit Image;
    begin
        // [GIVEN] A valid 1x1 PNG loaded via FromBase64
        // [WHEN]  GetHeight is called on the blank-shell stub
        // [THEN]  Returns 0 — the blank shell does not parse image headers
        ImageSys.FromBase64(Png1x1Lbl);
        Assert.AreEqual(0, ImageSys.GetHeight(),
            'Blank-shell Image.GetHeight() must return 0 (no header parsing in standalone mode)');
    end;

    [Test]
    procedure GetWidth_WithoutLoad_ReturnsZero()
    var
        ImageSys: Codeunit Image;
    begin
        // [GIVEN] An uninitialised Image variable
        // [THEN]  GetWidth returns 0 (type default)
        Assert.AreEqual(0, ImageSys.GetWidth(), 'Uninitialised Image.GetWidth() must return 0');
    end;

    [Test]
    procedure GetHeight_WithoutLoad_ReturnsZero()
    var
        ImageSys: Codeunit Image;
    begin
        // [GIVEN] An uninitialised Image variable
        // [THEN]  GetHeight returns 0 (type default)
        Assert.AreEqual(0, ImageSys.GetHeight(), 'Uninitialised Image.GetHeight() must return 0');
    end;

    // ── Blank-shell: Text-returning methods return empty string ───────────────

    [Test]
    procedure ToBase64_ReturnsEmpty_IsNoOp()
    var
        ImageSys: Codeunit Image;
        Result: Text;
    begin
        // [GIVEN] An uninitialised Image variable
        // [WHEN]  ToBase64 is called
        // [THEN]  Returns empty string (type default); no exception
        Result := ImageSys.ToBase64();
        Assert.AreEqual('', Result, 'Blank-shell Image.ToBase64() must return empty string');
    end;

    [Test]
    procedure GetFormatAsText_ReturnsEmpty_IsNoOp()
    var
        ImageSys: Codeunit Image;
        Result: Text;
    begin
        // [GIVEN] An uninitialised Image variable
        // [WHEN]  GetFormatAsText is called
        // [THEN]  Returns empty string (type default); no exception
        Result := ImageSys.GetFormatAsText();
        Assert.AreEqual('', Result, 'Blank-shell Image.GetFormatAsText() must return empty string');
    end;

    // ── Blank-shell: no throw on any input ─────────────────────────────────────

    [Test]
    procedure FromBase64_ValidPng_IsNoOp()
    var
        ImageSys: Codeunit Image;
    begin
        // [GIVEN] A valid base64-encoded PNG
        // [WHEN]  FromBase64 is called
        // [THEN]  No exception (blank shell ignores input)
        ImageSys.FromBase64(Png1x1Lbl);
    end;

    [Test]
    procedure FromBase64_EmptyString_IsNoOp()
    var
        ImageSys: Codeunit Image;
    begin
        // [GIVEN] Empty string
        // [WHEN]  FromBase64 is called
        // [THEN]  No exception (blank shell does not validate input)
        ImageSys.FromBase64('');
    end;

    [Test]
    procedure FromBase64_InvalidBase64_IsNoOp()
    var
        ImageSys: Codeunit Image;
    begin
        // [GIVEN] A base64 string decoding to non-image bytes ("hello")
        // [WHEN]  FromBase64 is called
        // [THEN]  No exception (blank shell does not decode or validate)
        ImageSys.FromBase64('aGVsbG8=');
    end;

    [Test]
    procedure Resize_IsNoOp()
    var
        ImageSys: Codeunit Image;
    begin
        // [GIVEN] An uninitialised Image variable
        // [WHEN]  Resize is called
        // [THEN]  No exception; GetWidth/GetHeight still return 0
        ImageSys.Resize(100, 200);
        Assert.AreEqual(0, ImageSys.GetWidth(),
            'Blank-shell Resize must not update dimensions — stub returns 0 regardless');
    end;
}
