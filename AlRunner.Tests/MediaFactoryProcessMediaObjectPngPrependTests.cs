// MediaFactoryProcessMediaObjectPngPrependTests — proves the #2570 fix: the mechanism
// MediaPatches.TryClassifyStructuralPng() drives, prepended to
// NavMediaFactory.ProcessMediaObject(Stream, bool, string) via Cecil (see
// NclCecilRewrite.cs), classifies a structurally valid PNG as "image/png" without decoding
// it, leaves an explicit mimeType or non-PNG content untouched, and raises BC's own
// "not a valid image" shape for a PNG whose chunk structure is corrupt.
//
// This is deliberately a RUNNER-INTERNAL claim, not a BC-behaviour one: it asserts that OUR
// C# structural-PNG classifier — the thing the Cecil prepend calls — returns the right
// answer for each input shape. Whether AL code that imports a PNG into a Media field
// actually stores it as image/png on real BC is a plain BC-behaviour claim and belongs
// upstream — see StefanMaron/BusinessCentral.AL.Language.Tests#138
// (tests/al-language/media/TestMediaPngImport.al), and the equivalent end-to-end proof run
// locally against that corpus branch (RED before this fix, GREEN after — see the PR
// description for #2570).
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class MediaFactoryProcessMediaObjectPngPrependTests
{
    [Fact]
    public void ProcessMediaObject_KeyIsCecilOwned()
    {
        Assert.Contains(
            "Microsoft.Dynamics.Nav.Runtime.Media.NavMediaFactory::ProcessMediaObject/3",
            NclCecilRewrite.CecilOwned);
    }

    // A minimal valid 1x1-pixel PNG (68 bytes): signature + IHDR(13) + IDAT(11) + IEND(0),
    // every chunk CRC verified independently with Python's zlib.crc32 — the same fixture
    // used in the upstream corpus PR (TestMediaPngImport.al).
    private static readonly byte[] ValidPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    // Same bytes with one byte of the IHDR chunk's CRC field flipped.
    private static readonly byte[] CorruptIhdrCrcPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAADFfptVAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private static readonly byte[] JpegSoi = { 0xFF, 0xD8, 0xFF };

    [Fact]
    public void ValidPng_EmptyMimeType_ClassifiesAsImagePng()
    {
        using var stream = new MemoryStream(ValidPng);

        var result = MediaPatches.TryClassifyStructuralPng(stream, "");

        Assert.Equal("image/png", result);
    }

    [Fact]
    public void ValidPng_NullMimeType_ClassifiesAsImagePng()
    {
        using var stream = new MemoryStream(ValidPng);

        var result = MediaPatches.TryClassifyStructuralPng(stream, null);

        Assert.Equal("image/png", result);
    }

    [Fact]
    public void ValidPng_RestoresStreamPositionAfterSniffing()
    {
        using var stream = new MemoryStream(ValidPng);
        stream.Position = 0;

        MediaPatches.TryClassifyStructuralPng(stream, null);

        // A "peek" must leave the stream exactly where the caller left it — the real
        // ProcessMediaObject body still needs to read this stream from the start.
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void ExplicitMimeType_ReturnsNull_EvenForValidPng()
    {
        // Mirrors ProcessMediaObject's own guard (string.IsNullOrEmpty(mimeType)) — a
        // caller-supplied mimeType must never be overridden.
        using var stream = new MemoryStream(ValidPng);

        var result = MediaPatches.TryClassifyStructuralPng(stream, "application/pdf");

        Assert.Null(result);
    }

    [Fact]
    public void NonPngContent_ReturnsNull_NotMisclassified()
    {
        using var stream = new MemoryStream(JpegSoi);

        var result = MediaPatches.TryClassifyStructuralPng(stream, null);

        Assert.Null(result);
    }

    [Fact]
    public void NonSeekableStream_ReturnsNull_DefersToExistingPath()
    {
        using var inner = new MemoryStream(ValidPng);
        using var nonSeekable = new NonSeekableWrapperStream(inner);

        var result = MediaPatches.TryClassifyStructuralPng(nonSeekable, null);

        Assert.Null(result);
    }

    [Fact]
    public void CorruptIhdrCrc_ThrowsWithSpecificDiagnostic()
    {
        using var stream = new MemoryStream(CorruptIhdrCrcPng);

        var ex = Assert.ThrowsAny<Exception>(() => MediaPatches.TryClassifyStructuralPng(stream, null));

        // Whichever exception shape NotAnImage() ends up constructing (NavImageLoadErrorException
        // wrapping ArgumentException in-process, or a bare ArgumentException if
        // Microsoft.Dynamics.Nav.Types has not been loaded yet in an isolated test run), the
        // specific diagnostic must be findable somewhere in the exception chain — proving
        // this fires for the CRC mismatch specifically, not any old exception.
        Assert.True(
            (ex.Message + " " + ex.InnerException?.Message).Contains("IHDR chunk CRC mismatch"),
            $"expected 'IHDR chunk CRC mismatch' somewhere in the exception chain, got: {ex}");
    }

    [Fact]
    public void Truncated_AfterSignature_ThrowsWithSpecificDiagnostic()
    {
        // The PNG signature alone (no chunks at all) — the shape
        // tests/runner-extras/standalone-suites/media-non-image-content used to use for its
        // "image content is refused by name" fixture before #2570 gave PNG its own path
        // (that test now uses a JPEG signature instead; see MncTests.Codeunit.al).
        using var stream = new MemoryStream(ValidPng.AsSpan(0, 8).ToArray());

        var ex = Assert.ThrowsAny<Exception>(() => MediaPatches.TryClassifyStructuralPng(stream, null));

        Assert.True(
            (ex.Message + " " + ex.InnerException?.Message).Contains("unexpected end of data after signature"),
            $"expected 'unexpected end of data after signature' somewhere in the exception chain, got: {ex}");
    }

    /// <summary>Wraps a seekable stream to report CanSeek=false, for the non-seekable test above.</summary>
    private sealed class NonSeekableWrapperStream : Stream
    {
        private readonly Stream _inner;
        public NonSeekableWrapperStream(Stream inner) => _inner = inner;
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    }
}
