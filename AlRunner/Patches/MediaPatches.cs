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
//   decoding. PNG turned out not to be one of those, on real BC: TryClassifyPngBySignature()
//   below is prepended (Cecil, see NclCecilRewrite.cs) to the START of
//   NavMediaFactory.ProcessMediaObject(Stream, bool, string) — BEFORE it decides whether to
//   call GetImageWithContentHeaderValidation at all. For content whose first 8 bytes are the
//   PNG signature and the caller passed no explicit mimeType, it OVERWRITES the mimeType
//   argument to "image/png" and lets the REST OF THE REAL, UNMODIFIED ProcessMediaObject body
//   run: with a non-empty mimeType it skips the whole GetImageWithContentHeaderValidation
//   try/catch, and NavMediaImage.IsSupportedMimeType is separately rewritten (elsewhere in
//   NclCecilRewrite.cs) to always answer false on this platform (its own System.Drawing-backed
//   statics are unavailable) — so "image/png" cascades straight past every
//   image/pdf/word/excel/powerpoint/onenote/text branch to BC's own generic fallback,
//   `new NavMediaBinaryFile(mediaStream, mimeType)`, unmodified. No decoded image is
//   fabricated: the bytes BC stores are exactly the bytes AL supplied, and
//   NavMediaBinaryFile's own ValidateContentHeader → NavMediaContentValidator.VerifyStreamData
//   is a pure managed byte-pattern check (PE/COFF/archive-executable guard) — no native
//   dependency, confirmed by reading its decompiled body.
//
//   MEASURED, not assumed: an earlier version of this file additionally validated every PNG
//   chunk's CRC32 and IHDR's declared width/height, reasoning that PNG validity is checkable
//   "structurally". Two full rounds of upstream corpus CI (27.0-28.4, all 8 legs each — see
//   StefanMaron/BusinessCentral.AL.Language.Tests#138) falsified that: BC accepts a PNG with a
//   wrong IHDR chunk CRC, a stream that is nothing but the 8-byte signature, a stream
//   truncated in the middle of the IHDR chunk, and a structurally complete PNG whose IHDR
//   declares width=0 — identically, all 8 legs, both rounds. BC's own PNG acceptance for a
//   Media field is the 8-byte signature match and NOTHING MORE: no chunk CRC check, no IHDR
//   presence/shape check, no dimension sanity-check. Matching that exactly (rather than being
//   STRICTER than BC, which would make the runner reject a PNG BC accepts — a defect in the
//   opposite direction from #2641) is why this file's classifier does only the signature
//   check. Content whose first 8 bytes are not the PNG signature is untouched — it still
//   reaches GetImageWithContentHeaderValidation exactly as before.
using System.Reflection;

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
    ///   first 8 bytes are not the PNG signature.</item>
    ///   <item>"image/png" — the content's first 8 bytes ARE the PNG signature; the caller
    ///   overwrites the mimeType argument with this and falls through to the real body.</item>
    ///   </list>
    /// Deliberately signature-only — see the file header for the corpus measurement
    /// (StefanMaron/BusinessCentral.AL.Language.Tests#138) that settled this: real BC
    /// accepts any signature-prefixed stream, valid PNG or not, so anything stricter here
    /// would make the runner reject content BC accepts.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static string? TryClassifyPngBySignature(object? mediaStreamObj, object? mimeTypeObj)
    {
        if (mimeTypeObj is string existingMimeType && existingMimeType.Length > 0)
            return null;
        if (mediaStreamObj is not Stream stream || !stream.CanSeek)
            return null;

        var origin = stream.Position;
        try
        {
            Span<byte> signature = stackalloc byte[8];
            int read = 0;
            while (read < signature.Length)
            {
                int n = stream.Read(signature.Slice(read));
                if (n <= 0) break;
                read += n;
            }
            if (read < signature.Length || !signature.SequenceEqual(PngSignature))
                return null;

            return "image/png";
        }
        finally
        {
            stream.Position = origin;
        }
    }
}
