// BcAppSymbolCache.TryReadSourceFile streams the dependency .app rather than reading it whole
// (#4938). Its callers (dependency xmlport metadata, RunObject kind lookup, dependency report
// metadata) hit Base Application, a ~100 MB package, once per object; a whole-file read each
// time was large-object-heap garbage that measurably raised peak RSS (#4935).
//
// Synthetic packages built here, so no platform artifacts and no Base Application floor
// (.claude/rules/no-base-app-in-csharp-tests.md).
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class BcAppSourceFileStreamingTests : IDisposable
{
    private const string StatedPath = "Bank/Probe.XmlPort.al";
    private const string ZipEntryPath = "src/Bank/Probe.XmlPort.al";
    private const string Source = "xmlport 4938 \"Probe\" { }";

    private readonly string _root = TestScratch.Dir("al-runner-4938-src-streaming");

    public BcAppSourceFileStreamingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private static byte[] ZipWith(params (string Name, byte[] Content, CompressionLevel Level)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content, level) in entries)
            {
                var e = zip.CreateEntry(name, level);
                using var s = e.Open();
                s.Write(content);
            }
        return ms.ToArray();
    }

    private static (string, byte[], CompressionLevel) SourceEntry()
        => (ZipEntryPath, Encoding.UTF8.GetBytes(Source), CompressionLevel.Optimal);

    /// <summary>The 40-byte NAVX header BC writes, payload at offset 40 (as in AppLoaderNeaRuntimePackageTests).</summary>
    private static byte[] Navx(byte[] payload)
    {
        var result = new byte[40 + payload.Length];
        "NAVX"u8.CopyTo(result);
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), 40u);
        BitConverter.TryWriteBytes(result.AsSpan(8, 4), 2u);
        Guid.NewGuid().ToByteArray().CopyTo(result, 12);
        BitConverter.TryWriteBytes(result.AsSpan(28, 8), (long)payload.Length);
        "NAVX"u8.CopyTo(result.AsSpan(36, 4));
        payload.CopyTo(result, 40);
        return result;
    }

    /// <summary>
    /// BC's .NEA runtime-package layer, encoded independently of the production decoder (same
    /// reasoning as AppLoaderNeaRuntimePackageTests.NeaEncode).
    /// </summary>
    private static byte[] NeaEncode(byte[] plain)
    {
        byte[] key = [0x0F, 0x0B, 0x51, 0x89, 0xB8, 0x78];
        var s = new byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)i;
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var body = new byte[plain.Length];
        for (int n = 0, i = 0, j = 0; n < plain.Length; n++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            body[n] = (byte)(plain[n] ^ s[(s[i] + s[j]) & 0xFF]);
        }
        byte[] header = [0x2E, 0x4E, 0x45, 0x41, 0x00, 0x00, 0x00, 0x01];
        var result = new byte[header.Length + body.Length];
        header.CopyTo(result, 0);
        body.CopyTo(result, header.Length);
        return result;
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    /// The property #4938 is about: reading one small source file out of a large package
    /// allocates on the order of that file, not of the package. The padding entry is STORED so
    /// the file on disk really is that large. A whole-file read allocates at least the file size.
    /// </summary>
    [Fact]
    public void ReadingOneSourceFile_AllocatesFarLessThanThePackageSize()
    {
        const int padding = 48 * 1024 * 1024;
        var app = Write("padded.app", Navx(ZipWith(
            ("resources/padding.bin", new byte[padding], CompressionLevel.NoCompression),
            SourceEntry())));
        var fileSize = new FileInfo(app).Length;
        Assert.True(fileSize > padding, $"fixture is {fileSize} bytes, expected > {padding}");

        // Warm-up so JIT and first-call allocations are not charged to the measured call.
        var warm = Write("warm.app", Navx(ZipWith(SourceEntry())));
        Assert.Equal(Source, BcAppSymbolCache.TryReadSourceFile(warm, StatedPath));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var read = BcAppSymbolCache.TryReadSourceFile(app, StatedPath);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(Source, read);
        Assert.True(allocated < fileSize / 16,
            $"TryReadSourceFile allocated {allocated:N0} bytes to read {Source.Length} chars out of a {fileSize:N0}-byte package");
    }

    [Fact]
    public void NavxPrefixedPackage_ReturnsTheSourceText()
    {
        var app = Write("navx.app", Navx(ZipWith(SourceEntry())));
        Assert.Equal(Source, BcAppSymbolCache.TryReadSourceFile(app, StatedPath));
        Assert.Null(BcAppSymbolCache.TryReadSourceFile(app, "Bank/NotShipped.XmlPort.al"));
    }

    [Fact]
    public void NeaRuntimePackage_ReturnsTheSourceText()
    {
        var app = Write("runtime.app", Navx(NeaEncode(ZipWith(SourceEntry()))));
        Assert.Equal(Source, BcAppSymbolCache.TryReadSourceFile(app, StatedPath));
        Assert.Null(BcAppSymbolCache.TryReadSourceFile(app, "Bank/NotShipped.XmlPort.al"));
    }

    /// <summary>R2R wrapper: the outer package carries no src/, the nested root-level .app does.</summary>
    [Fact]
    public void NestedR2RWrapper_ReturnsTheSourceTextFromTheInnerPackage()
    {
        var inner = Navx(ZipWith(SourceEntry()));
        var app = Write("wrapper.app", Navx(ZipWith(
            ("NavxManifest.xml", Encoding.UTF8.GetBytes("<Package/>"), CompressionLevel.Optimal),
            ("Inner.app", inner, CompressionLevel.Optimal))));
        Assert.Equal(Source, BcAppSymbolCache.TryReadSourceFile(app, StatedPath));
        Assert.Null(BcAppSymbolCache.TryReadSourceFile(app, "Bank/NotShipped.XmlPort.al"));
    }

    /// <summary>A file that is not a zip at all still answers null (logged), never throws.</summary>
    [Fact]
    public void UnreadablePackage_AnswersNull()
    {
        var app = Write("garbage.app", Encoding.UTF8.GetBytes("not a zip"));
        Assert.Null(BcAppSymbolCache.TryReadSourceFile(app, StatedPath));
    }
}
