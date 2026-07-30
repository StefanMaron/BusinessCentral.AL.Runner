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
    /// BC's own <c>NavImageLoadErrorException</c> wrapping an <c>ArgumentException</c> — the
    /// exact shape NavMediaFactory.ProcessMediaObject's `when (ex.InnerException is
    /// ArgumentException)` filter looks for. Any other type or inner type means the media
    /// write fails instead of falling back to application/octet-stream.
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
                return (Exception)_navImageLoadErrorCtor.Invoke(new object[] { because, inner });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[MediaPatches] could not construct NavImageLoadErrorException ({ex.GetType().Name}) — "
                + "non-image media will fail to store rather than falling back to octet-stream");
        }
        return inner;
    }
}
