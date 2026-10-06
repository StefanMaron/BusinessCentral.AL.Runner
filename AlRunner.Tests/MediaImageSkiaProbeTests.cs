// MediaImageSkiaProbeTests — BC 29 probes media content through SkiaSharp off Windows (#5382).
//
// BC 29's NavMediaFactory.ProcessMediaObject calls NavMediaImageSkia.TryCreate -> TryProbe
// (SKCodec.Create) on non-Windows hosts, and BC ships only the Windows libSkiaSharp, so without a
// replacement every media write dies in SkiaSharp's type initializer. MediaPatches.NavMediaImageSkia_TryProbe
// is the replacement; the AL-level proof is tests/runner-extras/standalone-suites/media-non-image-content
// (on a BC 29 leg). These pin the two directions of the helper itself, each so that the obvious wrong
// implementation fails one: "always false" passes the first and fails the second, "always refuse" the reverse.
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class MediaImageSkiaProbeTests
{
    [Theory]
    [InlineData("<pageworks><text>not an image</text></pageworks>")]
    [InlineData("%PDF-1.7 not an image")]
    [InlineData("")]
    public void NonImageContent_ProbesAsNotAnImage_SoBcStoresItAsOctetStream(string content)
    {
        var ok = MediaPatches.NavMediaImageSkia_TryProbe(
            System.Text.Encoding.UTF8.GetBytes(content), out var format, out var width, out var height);

        Assert.False(ok);
        Assert.Equal((0, 0, 0), (format, width, height));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 })]                                        // JPEG
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 })]         // PNG
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' })] // GIF
    public void ImageContent_IsRefusedByName_NotDecodedAndNotStored(byte[] content)
    {
        var oos = Assert.Throws<RunnerOutOfScopeException>(() =>
            MediaPatches.NavMediaImageSkia_TryProbe(content, out _, out _, out _));

        Assert.Equal("NavMediaImageSkia.TryProbe", oos.Api);
        Assert.StartsWith("media-image-decode", oos.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NullBytes_AreRefusedRatherThanProbedAsNonImage()
    {
        Assert.Throws<ArgumentNullException>(() =>
            MediaPatches.NavMediaImageSkia_TryProbe(null, out _, out _, out _));
    }
}
