namespace AlRunner.Runtime;

/// <summary>
/// In-memory Image mock replacing the auto-stub for Codeunit "Image" (ID 3971).
///
/// Image format detection + header parsing — no external dependencies.
/// Supports PNG, JPEG, GIF, and BMP; each encodes width/height in the first ~30 bytes.
///
///   PNG  — magic 0x89 P N G; IHDR chunk width at bytes 16–19, height at 20–23 (big-endian)
///   JPEG — magic 0xFF 0xD8; SOFn segment (0xFFC0/C1/C2) has height at [5..6], width at [7..8]
///   GIF  — magic "GIF8"; width at bytes 6–7, height at 8–9 (little-endian)
///   BMP  — magic "BM"; width at bytes 18–21, height at 22–25 (little-endian signed)
///
/// <c>FromStream</c> / <c>FromBase64</c> load bytes, detect format, parse dimensions, and throw
/// an <c>InvalidOperationException</c> (surfaced as an AL runtime error) when the format is
/// unrecognised or the stream is empty/invalid — matching BC behaviour.
/// </summary>
public sealed class MockImage
{
    private int _width;
    private int _height;
    private byte[] _data = Array.Empty<byte>();
    private string _format = "Png";

    // ── FromStream ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Load image data from an InStream. Throws on empty or unrecognised format.
    /// BC method: Image.FromStream(InStream).
    /// </summary>
    public void FromStream(MockInStream stream)
    {
        if (stream == null) ThrowInvalidImage("null stream");

        // Read all available bytes from the stream.
        var bytes = ReadAllBytes(stream!);
        LoadFromBytes(bytes);
    }

    // ── FromBase64 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Load image data from a base64-encoded string. Throws on empty or unrecognised content.
    /// BC method: Image.FromBase64(Base64Text: Text).
    /// </summary>
    public void FromBase64(string base64Text)
    {
        if (string.IsNullOrEmpty(base64Text))
            ThrowInvalidImage("empty base64 string");

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64Text);
        }
        catch (FormatException ex)
        {
            ThrowInvalidImage($"invalid base64: {ex.Message}");
            return; // unreachable — ThrowInvalidImage always throws
        }

        LoadFromBytes(bytes);
    }

    // ── GetWidth / GetHeight ──────────────────────────────────────────────────────

    /// <summary>Returns the image width in pixels. BC method: Image.GetWidth(): Integer.</summary>
    public int GetWidth() => _width;

    /// <summary>Returns the image height in pixels. BC method: Image.GetHeight(): Integer.</summary>
    public int GetHeight() => _height;

    // ── Resize ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Records new target dimensions. Subsequent GetWidth/GetHeight reflect them.
    /// BC method: Image.Resize(Width: Integer, Height: Integer).
    /// </summary>
    public void Resize(int width, int height)
    {
        _width = width;
        _height = height;
    }

    // ── Save ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Write the stored image bytes back to an OutStream.
    /// BC method: Image.Save(OutStream: OutStream).
    /// </summary>
    public void Save(MockOutStream stream)
    {
        if (stream == null) return;
        if (_data.Length > 0)
            stream.Write(_data, 0, _data.Length);
    }

    // ── ToBase64 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the stored image bytes as a base64 string.
    /// BC method: Image.ToBase64(): Text.
    /// </summary>
    public string ToBase64()
    {
        return _data.Length > 0 ? Convert.ToBase64String(_data) : string.Empty;
    }

    // ── GetFormatAsText ───────────────────────────────────────────────────────────

    /// <summary>Returns the detected format name ("Png", "Jpeg", "Gif", "Bmp").</summary>
    public string GetFormatAsText() => _format;

    // ── Format-detection + header parsing ────────────────────────────────────────

    private void LoadFromBytes(byte[] bytes)
    {
        if (bytes.Length == 0)
            ThrowInvalidImage("empty byte array — not a valid image");

        if (TryParsePng(bytes, out int w, out int h))
        {
            _width = w; _height = h; _data = bytes; _format = "Png";
            return;
        }
        if (TryParseJpeg(bytes, out w, out h))
        {
            _width = w; _height = h; _data = bytes; _format = "Jpeg";
            return;
        }
        if (TryParseGif(bytes, out w, out h))
        {
            _width = w; _height = h; _data = bytes; _format = "Gif";
            return;
        }
        if (TryParseBmp(bytes, out w, out h))
        {
            _width = w; _height = h; _data = bytes; _format = "Bmp";
            return;
        }

        ThrowInvalidImage("unrecognised image format — supported formats: PNG, JPEG, GIF, BMP");
    }

    // PNG: magic bytes 0..7 = { 137, 80, 78, 71, 13, 10, 26, 10 }
    //      IHDR chunk: bytes 8..11 = length (0x0000000D), bytes 12..15 = "IHDR"
    //      width BE: bytes 16..19, height BE: bytes 20..23
    private static bool TryParsePng(byte[] b, out int width, out int height)
    {
        width = height = 0;
        if (b.Length < 24) return false;
        if (b[0] != 137 || b[1] != 80 || b[2] != 78 || b[3] != 71 ||
            b[4] != 13  || b[5] != 10 || b[6] != 26 || b[7] != 10)
            return false;

        width  = ReadInt32BE(b, 16);
        height = ReadInt32BE(b, 20);
        return width > 0 && height > 0;
    }

    // JPEG: magic 0xFF 0xD8; scan for SOF0/SOF1/SOF2 marker (0xFF 0xCx)
    //       SOFn payload: [marker(2)] [length(2)] [precision(1)] [height(2)] [width(2)]
    private static bool TryParseJpeg(byte[] b, out int width, out int height)
    {
        width = height = 0;
        if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8) return false;

        int pos = 2;
        while (pos + 4 < b.Length)
        {
            if (b[pos] != 0xFF) break;
            byte marker = b[pos + 1];
            // SOF markers: 0xC0..0xC3, 0xC5..0xC7, 0xC9..0xCB, 0xCD..0xCF
            if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                if (pos + 9 >= b.Length) break;
                height = ReadInt16BE(b, pos + 5);
                width  = ReadInt16BE(b, pos + 7);
                return width > 0 && height > 0;
            }
            // Skip segment: length at pos+2 (includes 2-byte length field itself)
            int len = ReadInt16BE(b, pos + 2);
            if (len < 2) break;
            pos += 2 + len;
        }
        return false;
    }

    // GIF: magic "GIF87a" or "GIF89a" (6 bytes)
    //      logical screen descriptor: width LE at [6..7], height LE at [8..9]
    private static bool TryParseGif(byte[] b, out int width, out int height)
    {
        width = height = 0;
        if (b.Length < 10) return false;
        if (b[0] != 'G' || b[1] != 'I' || b[2] != 'F' ||
            b[3] != '8' || (b[4] != '7' && b[4] != '9') || b[5] != 'a')
            return false;

        width  = b[6] | (b[7] << 8);
        height = b[8] | (b[9] << 8);
        return width > 0 && height > 0;
    }

    // BMP: magic "BM" (2 bytes); DIB header starts at byte 14
    //      BITMAPINFOHEADER: width (signed LE) at [18..21], height (signed LE) at [22..25]
    private static bool TryParseBmp(byte[] b, out int width, out int height)
    {
        width = height = 0;
        if (b.Length < 26) return false;
        if (b[0] != 'B' || b[1] != 'M') return false;

        width  = Math.Abs(ReadInt32LE(b, 18));
        height = Math.Abs(ReadInt32LE(b, 22));
        return width > 0 && height > 0;
    }

    // ── Binary helpers ────────────────────────────────────────────────────────────

    private static int ReadInt32BE(byte[] b, int offset)
        => (b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3];

    private static int ReadInt16BE(byte[] b, int offset)
        => (b[offset] << 8) | b[offset + 1];

    private static int ReadInt32LE(byte[] b, int offset)
        => b[offset] | (b[offset + 1] << 8) | (b[offset + 2] << 16) | (b[offset + 3] << 24);

    private static byte[] ReadAllBytes(MockInStream stream)
    {
        var buf = new List<byte>();
        var chunk = new byte[4096];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
                buf.Add(chunk[i]);
        }
        return buf.ToArray();
    }

    private static void ThrowInvalidImage(string detail)
        => throw new InvalidOperationException($"The stream does not contain a valid image — {detail}");
}
