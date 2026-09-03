// MediaPatches — content-header validation for Media/MediaSet fields without System.Drawing.
//
// WHY
//   BC decides what a Media field holds by trying to decode it as an image:
//       NavMediaFactory.ProcessMediaObject(stream, saveStream, mimeType)
//         try  { NavMediaImage.GetImageWithContentHeaderValidation(stream) }
//         catch (NavImageLoadErrorException ex) when (ex.InnerException is ArgumentException)
//              { mimeType = "application/octet-stream"; }
//   That catch is how NON-image media (a report layout template, a PDF, any blob) gets
//   stored at all. On Windows it fires because System.Drawing's Image.FromStream throws
//   ArgumentException for content it does not recognise.
//
//   On Linux there is no libgdiplus and System.Drawing.Common is unsupported, so
//   Image.FromStream throws PlatformNotSupportedException instead. BC's exception mapper
//   turns that into a NavImageLoadErrorException whose InnerException is NOT an
//   ArgumentException — so the `when` filter does not match, the fallback never runs, and
//   EVERY media write failed with "The media object could not be loaded because it is not a
//   valid image type, such as JPEG, GIF, or PNG", image or not. Publishing a report layout
//   (bytes that were never meant to be an image) hit exactly that.
//
// WHAT THIS DOES
//   Replaces the validation with a magic-byte sniff, and answers in the shape BC's own
//   control flow expects:
//     * content that is NOT a recognised image  → NavImageLoadErrorException wrapping an
//       ArgumentException, i.e. precisely what Windows produces, so BC's own
//       octet-stream fallback runs and the media stores;
//     * content that IS a recognised image      → a named refusal, because this platform
//       genuinely cannot decode it and answering with a fake would let a test assert
//       against image dimensions that were never read.
//
//   Sniffing rather than decoding is faithful for the first case: BC only needs to know
//   "is this an image", and a file whose header is not one of the image signatures is not
//   one, on any platform.
//
// #2570 — PNG IMPORT WITHOUT A DECODER
//   The refusal above is the right answer for a format whose validity genuinely depends on
//   decoding — but PNG's validity is checkable STRUCTURALLY: the 8-byte signature, chunk
//   ordering, a per-chunk CRC32, and a well-formed IHDR. TryClassifyStructuralPng() below is
//   prepended (Cecil, see NclCecilRewrite.cs) to the START of
//   NavMediaFactory.ProcessMediaObject(Stream, bool, string) — BEFORE it decides whether to
//   call GetImageWithContentHeaderValidation at all. For content that is a structurally valid
//   PNG and the caller passed no explicit mimeType, it OVERWRITES the mimeType argument to
//   "image/png" and lets the REST OF THE REAL, UNMODIFIED ProcessMediaObject body run: with a
//   non-empty mimeType it skips the whole GetImageWithContentHeaderValidation try/catch, and
//   NavMediaImage.IsSupportedMimeType is separately rewritten (elsewhere in
//   NclCecilRewrite.cs) to always answer false on this platform (its own System.Drawing-backed
//   statics are unavailable) — so "image/png" cascades straight past every
//   image/pdf/word/excel/powerpoint/onenote/text branch to BC's own generic fallback,
//   `new NavMediaBinaryFile(mediaStream, mimeType)`, unmodified. No decoded image is
//   fabricated: the bytes BC stores are exactly the bytes AL supplied, and
//   NavMediaBinaryFile's own ValidateContentHeader → NavMediaContentValidator.VerifyStreamData
//   is a pure managed byte-pattern check (PE/COFF/archive-executable guard) — no native
//   dependency, confirmed by reading its decompiled body.
//
//   This narrows the refusal for PNG only. Content whose first 8 bytes are not the PNG
//   signature is untouched — it still reaches GetImageWithContentHeaderValidation exactly as
//   before. Content that looks like a PNG but fails structural validation (a bad chunk CRC, a
//   malformed IHDR, truncated data) raises BC's own "not a valid image" error
//   (NavImageLoadErrorException wrapping ArgumentException, thrown via the same NotAnImage()
//   helper below) INSTEAD of silently storing invalid bytes as image/png.
//
//   Not a claim of byte-for-byte equivalence with a real GDI+ decoder: a PNG that passes this
//   structural check but that GDI+ would reject for some decoder-specific reason would be
//   accepted here and refused on a real tier. See docs/scope.md.
using System.Buffers.Binary;
using System.Reflection;
using System.Text;

namespace AlRunner.Patches;

public static class MediaPatches
{
    /// <summary>
    /// Replacement for <c>NavMediaImage.GetImageWithContentHeaderValidation(Stream)</c>.
    /// Never returns: either it refuses as "not an image" in BC's expected shape, or it
    /// refuses by name because the image cannot be decoded here.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static object? NavMediaImage_GetImageWithContentHeaderValidation(object? contentStream)
    {
        if (contentStream is not Stream stream)
            throw NotAnImage("the media content is not a readable stream");

        if (!stream.CanSeek)
            throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                "NavMediaImage.GetImageWithContentHeaderValidation",
                "media-content-sniff — the media content stream is not seekable, so its header "
                + "cannot be inspected without consuming the content BC is about to store. "
                + "See docs/scope.md");

        var origin = stream.Position;
        Span<byte> header = stackalloc byte[12];
        int read = 0;
        try
        {
            int n;
            while (read < header.Length && (n = stream.Read(header.Slice(read))) > 0) read += n;
        }
        finally { stream.Position = origin; }

        if (!LooksLikeImage(header[..read]))
            throw NotAnImage("the media content header matches no known image signature");

        throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
            "NavMediaImage.GetImageWithContentHeaderValidation",
            "media-image-decode — the content IS an image, but decoding one needs "
            + "System.Drawing, which has no support on this platform (no libgdiplus). Non-image "
            + "media stores normally. See docs/scope.md");
    }

    /// <summary>Image signatures BC's own supported set covers (JPEG, PNG, GIF, BMP, TIFF, ICO).</summary>
    private static bool LooksLikeImage(ReadOnlySpan<byte> h)
    {
        static bool Starts(ReadOnlySpan<byte> h, params byte[] sig)
            => h.Length >= sig.Length && h[..sig.Length].SequenceEqual(sig);

        return Starts(h, 0xFF, 0xD8, 0xFF)                                     // JPEG
            || Starts(h, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)       // PNG
            || Starts(h, (byte)'G', (byte)'I', (byte)'F', (byte)'8')           // GIF87a / GIF89a
            || Starts(h, (byte)'B', (byte)'M')                                 // BMP
            || Starts(h, 0x49, 0x49, 0x2A, 0x00)                               // TIFF little-endian
            || Starts(h, 0x4D, 0x4D, 0x00, 0x2A)                               // TIFF big-endian
            || Starts(h, 0x00, 0x00, 0x01, 0x00);                              // ICO
    }

    private static Type? _navImageLoadErrorType;
    private static ConstructorInfo? _navImageLoadErrorCtor;

    /// <summary>
    /// BC's own message for this exact shape (real service tier's <c>Lang.MediaImageLoadError</c>
    /// resource string — this is the literal text a real tier shows, quoted from empirical
    /// observation: pre-#1... the Linux PlatformNotSupportedException path let this exact
    /// NavImageLoadErrorException(ArgumentException) propagate out UNCAUGHT for every media
    /// write, image or not, which is how the text was captured). Every caller of NotAnImage()
    /// must use this as the OUTER exception's message — `because` below is diagnostic detail
    /// for the (usually swallowed) INNER exception only, never a substitute for BC's own text.
    /// </summary>
    internal const string MediaImageLoadErrorMessage =
        "The media object could not be loaded because it is not a valid image type, such as JPEG, GIF, or PNG";

    /// <summary>
    /// BC's own <c>NavImageLoadErrorException</c> wrapping an <c>ArgumentException</c> — the
    /// exact shape NavMediaFactory.ProcessMediaObject's `when (ex.InnerException is
    /// ArgumentException)` filter looks for. Any other type or inner type means the media
    /// write fails instead of falling back to application/octet-stream. The OUTER exception's
    /// message is always <see cref="MediaImageLoadErrorMessage"/> — BC's own text, and the one
    /// callers actually see when this propagates uncaught (e.g. #2570's corrupt-PNG path,
    /// which throws this OUTSIDE ProcessMediaObject's catch). <paramref name="because"/> is a
    /// diagnostic detail carried on the INNER ArgumentException only.
    /// </summary>
    private static Exception NotAnImage(string because)
    {
        var inner = new ArgumentException(because);
        try
        {
            if (_navImageLoadErrorCtor == null)
            {
                _navImageLoadErrorType = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types")?
                    .GetType("Microsoft.Dynamics.Nav.Types.Exceptions.NavImageLoadErrorException");
                _navImageLoadErrorCtor = _navImageLoadErrorType?.GetConstructor(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    binder: null, types: new[] { typeof(string), typeof(Exception) }, modifiers: null);
            }
            if (_navImageLoadErrorCtor != null)
                return (Exception)_navImageLoadErrorCtor.Invoke(new object[] { MediaImageLoadErrorMessage, inner });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[MediaPatches] could not construct NavImageLoadErrorException ({ex.GetType().Name}) — "
                + "non-image media will fail to store rather than falling back to octet-stream");
        }
        return inner;
    }

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>
    /// #2570 — prepended to the START of NavMediaFactory.ProcessMediaObject(Stream, bool,
    /// string) via Cecil (see NclCecilRewrite.cs). Returns:
    ///   <list type="bullet">
    ///   <item>null — no change; the original body runs exactly as before. Covers: an
    ///   explicit mimeType was already given (mirrors the real body's own
    ///   <c>string.IsNullOrEmpty(mimeType)</c> guard), the stream cannot be sniffed, or the
    ///   first 8 bytes are not the PNG signature at all.</item>
    ///   <item>"image/png" — the content IS a structurally valid PNG; the caller overwrites
    ///   the mimeType argument with this and falls through to the real body.</item>
    ///   </list>
    /// Throws BC's own "not a valid image" shape (see NotAnImage() above) when the PNG
    /// signature is present but the chunk structure is corrupt — raised here, BEFORE the
    /// original body's own try/catch exists, so it propagates as a real error rather than
    /// being caught by anything.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static string? TryClassifyStructuralPng(object? mediaStreamObj, object? mimeTypeObj)
    {
        if (mimeTypeObj is string existingMimeType && existingMimeType.Length > 0)
            return null;
        if (mediaStreamObj is not Stream stream || !stream.CanSeek)
            return null;

        var origin = stream.Position;
        try
        {
            Span<byte> signature = stackalloc byte[8];
            if (!TryReadFully(stream, signature) || !signature.SequenceEqual(PngSignature))
                return null;

            string? reason = ValidatePngChunkStructure(stream);
            if (reason != null)
                throw NotAnImage($"the PNG content is structurally invalid: {reason}");

            return "image/png";
        }
        finally
        {
            stream.Position = origin;
        }
    }

    private static bool TryReadFully(Stream stream, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer.Slice(read));
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    /// <summary>
    /// Validates every PNG chunk following the 8-byte signature (already consumed by the
    /// caller): 4-byte big-endian length, 4-byte ASCII type, `length` bytes of chunk data,
    /// then a 4-byte big-endian CRC32 over (type+data) — the exact algorithm the PNG
    /// specification requires (ISO-HDLC / zlib polynomial, the same CRC32 gzip and zip use).
    /// Requires the first chunk to be IHDR (length 13, positive width and height) and an
    /// IEND chunk (length 0) to terminate. Returns null when every chunk validates,
    /// otherwise a short human-readable reason.
    /// </summary>
    private static string? ValidatePngChunkStructure(Stream stream)
    {
        bool sawIhdr = false;
        bool sawIend = false;
        const int maxChunks = 4096; // guards a maliciously/accidentally unbounded chunk count
        // Allocated ONCE outside the loop (CA2014): a stackalloc's lifetime is the whole
        // method, not just the loop iteration that created it, so allocating fresh spans
        // per iteration would accumulate stack usage across up to maxChunks iterations.
        Span<byte> lengthBuf = stackalloc byte[4];
        Span<byte> typeBuf = stackalloc byte[4];
        Span<byte> crcBuf = stackalloc byte[4];
        for (int i = 0; i < maxChunks && !sawIend; i++)
        {
            if (!TryReadFully(stream, lengthBuf))
                return sawIhdr ? "unexpected end of data before IEND" : "unexpected end of data after signature";
            uint length = BinaryPrimitives.ReadUInt32BigEndian(lengthBuf);
            if (length > 0x7FFFFFFF)
                return "chunk length out of range";

            if (!TryReadFully(stream, typeBuf))
                return "unexpected end of data reading chunk type";
            string type = Encoding.ASCII.GetString(typeBuf);

            if (!sawIhdr && type != "IHDR")
                return "first chunk is not IHDR";

            byte[] data = length == 0 ? Array.Empty<byte>() : new byte[length];
            if (length > 0 && !TryReadFully(stream, data))
                return $"unexpected end of data reading {type} chunk body";

            if (!TryReadFully(stream, crcBuf))
                return $"unexpected end of data reading {type} chunk CRC";
            uint storedCrc = BinaryPrimitives.ReadUInt32BigEndian(crcBuf);
            uint computedCrc = Crc32(typeBuf, data);
            if (computedCrc != storedCrc)
                return $"{type} chunk CRC mismatch";

            if (type == "IHDR")
            {
                if (length != 13) return "IHDR chunk has the wrong length";
                uint width = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4));
                uint height = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4));
                if (width == 0 || height == 0) return "IHDR declares zero width or height";
                sawIhdr = true;
            }
            else if (type == "IEND")
            {
                sawIend = true;
            }
        }

        if (!sawIhdr) return "no IHDR chunk found";
        if (!sawIend) return "no IEND chunk found";
        return null;
    }

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    /// <summary>PNG's CRC32 (ISO-HDLC / zlib polynomial) over the concatenation of two spans.</summary>
    private static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte by in a) c = Crc32Table[(c ^ by) & 0xFF] ^ (c >> 8);
        foreach (byte by in b) c = Crc32Table[(c ^ by) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
