// MediaFactoryProcessMediaObjectPngPrependTests — proves the #2570 fix: the mechanism
// MediaPatches.TryClassifyPngBySignature() drives, prepended to
// NavMediaFactory.ProcessMediaObject(Stream, bool, string) via Cecil (see
// NclCecilRewrite.cs), classifies any PNG-signature-prefixed stream as "image/png" without
// decoding it, and leaves an explicit mimeType or non-PNG content untouched.
//
// Signature-only, deliberately: an earlier version of this classifier additionally
// validated every PNG chunk's CRC32 and IHDR's declared width/height. Two full rounds of
// upstream corpus CI (StefanMaron/BusinessCentral.AL.Language.Tests#138, 27.0-28.4, all 8
// legs each) measured that real BC accepts a PNG with a wrong IHDR chunk CRC, a stream that
// is nothing but the 8-byte signature, a stream truncated mid-IHDR-chunk, and a
// structurally complete PNG with IHDR width=0 — identically, both rounds. Matching BC
// exactly (rather than being stricter, which would make the runner reject a PNG BC
// accepts) is why this file's classifier — and these tests — only check the signature.
//
// This is deliberately a RUNNER-INTERNAL claim, not a BC-behaviour one: it asserts that OUR
// C# classifier — the thing the Cecil prepend calls — returns the right answer for each
// input shape. Whether AL code that imports a PNG into a Media field actually stores it as
// image/png on real BC is a plain BC-behaviour claim and belongs upstream — see
// StefanMaron/BusinessCentral.AL.Language.Tests#138
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

    // A minimal valid 1x1-pixel PNG (68 bytes): signature + IHDR(13) + IDAT(11) + IEND(0).
    private static readonly byte[] ValidPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    // Same bytes with one byte of the IHDR chunk's CRC field flipped — BC accepts this
    // (measured, corpus #138 round 1); still just the signature that matters here.
    private static readonly byte[] CorruptIhdrCrcPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAADFfptVAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    // Just the 8-byte PNG signature, nothing else — BC accepts this too (measured, corpus
    // #138 round 2).
    private static readonly byte[] SignatureOnlyPng = ValidPng.AsSpan(0, 8).ToArray();

    private static readonly byte[] JpegSoi = { 0xFF, 0xD8, 0xFF };

    [Fact]
    public void ValidPng_EmptyMimeType_ClassifiesAsImagePng()
    {
        using var stream = new MemoryStream(ValidPng);

        var result = MediaPatches.TryClassifyPngBySignature(stream, "");

        Assert.Equal("image/png", result);
    }

    [Fact]
    public void ValidPng_NullMimeType_ClassifiesAsImagePng()
    {
        using var stream = new MemoryStream(ValidPng);

        var result = MediaPatches.TryClassifyPngBySignature(stream, null);

        Assert.Equal("image/png", result);
    }

    [Fact]
    public void ValidPng_RestoresStreamPositionAfterSniffing()
    {
        using var stream = new MemoryStream(ValidPng);
        stream.Position = 0;

        MediaPatches.TryClassifyPngBySignature(stream, null);

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

        var result = MediaPatches.TryClassifyPngBySignature(stream, "application/pdf");

        Assert.Null(result);
    }

    [Fact]
    public void NonPngContent_ReturnsNull_NotMisclassified()
    {
        using var stream = new MemoryStream(JpegSoi);

        var result = MediaPatches.TryClassifyPngBySignature(stream, null);

        Assert.Null(result);
    }

    [Fact]
    public void NonSeekableStream_ReturnsNull_DefersToExistingPath()
    {
        using var inner = new MemoryStream(ValidPng);
        using var nonSeekable = new NonSeekableWrapperStream(inner);

        var result = MediaPatches.TryClassifyPngBySignature(nonSeekable, null);

        Assert.Null(result);
    }

    [Fact]
    public void CorruptIhdrCrc_StillClassifiesAsImagePng()
    {
        // MEASURED (corpus #138 round 1): real BC accepts this. The classifier must not
        // reject it either — rejecting a PNG BC accepts is the exact defect this file's
        // simplification exists to avoid.
        using var stream = new MemoryStream(CorruptIhdrCrcPng);

        var result = MediaPatches.TryClassifyPngBySignature(stream, null);

        Assert.Equal("image/png", result);
    }

    [Fact]
    public void SignatureOnly_StillClassifiesAsImagePng()
    {
        // MEASURED (corpus #138 round 2): real BC accepts a stream that is nothing but the
        // 8-byte PNG signature. Same reasoning as CorruptIhdrCrc_StillClassifiesAsImagePng.
        using var stream = new MemoryStream(SignatureOnlyPng);

        var result = MediaPatches.TryClassifyPngBySignature(stream, null);

        Assert.Equal("image/png", result);
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
