// AppLoaderR2rChunkCacheIdentityTests — issue #2955.
//
// AppLoader.ExtractAllDllPaths keyed its PERSISTED, CROSS-PROCESS r2r-chunks cache on
// `{fullPath}|{Length}|{LastWriteTimeUtc.Ticks}` — a filesystem stat standing in for the
// package's content, the same substitution #1820 removed from BcAppSymbolCache.Get,
// #2754/#2847 removed from the AL-output key's dependency terms and #2846 removed from
// ComputeSourceWorkspaceKey. What this cache holds is the R2R DLL chunks a dependency's
// CODE is loaded from, so the two directions cost:
//
//   * Wrong answer — same path, same size, same mtime, different bytes serves the previous
//     package's extracted DLLs. Nothing fails; the run just links against code the current
//     package does not contain.
//   * Unconditional miss — CI re-downloads every platform/test-toolkit .app on every run,
//     so the mtime is fresh even when the bytes are byte-for-byte identical, and every
//     entry MISSes regardless of content (#1815's argument, never applied here).
//
// The packages below are synthesized rather than taken from a provisioned platform dir, so
// these arms run everywhere — a stat collision has to be CONSTRUCTED (identical length,
// identical mtime, different bytes) and a real .app cannot be edited into that shape.
// ExtractAllDlls only copies `publishedartifacts/*.dll` zip entries out; it never parses
// them as PE, so a chunk payload here is a byte pattern whose identity the assertions can
// state exactly.
using System.IO.Compression;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

// CacheRoots is process-wide mutable static state (see CacheRootsSerialCollection's header).
[Collection(CacheRootsSerialCollection.Name)]
public sealed class AppLoaderR2rChunkCacheIdentityTests
{
    private const string ChunkEntry = "publishedartifacts/net8.0/Some.App.dll";

    /// <summary>
    /// A minimal package in the shape ExtractAllDlls reads: a plain ZIP (NAVX-prefixed and
    /// bare are both accepted — ReadNavxOffsetFromStream answers 0 for a bare one) carrying
    /// one stored, uncompressed <c>publishedartifacts/*.dll</c> entry. Stored, so two
    /// packages whose payloads differ only in their bytes come out the same total LENGTH —
    /// which is half of the stat collision under test.
    /// </summary>
    private static void WritePackage(string path, byte[] chunkPayload)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false);
        var entry = zip.CreateEntry(ChunkEntry, CompressionLevel.NoCompression);
        entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var s = entry.Open();
        s.Write(chunkPayload, 0, chunkPayload.Length);
    }

    private static byte[] Payload(byte fill, int length = 4096)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, fill);
        // Distinct leading marker so a truncated/zeroed read cannot pass an equality check
        // by accident.
        bytes[0] = 0x4D; bytes[1] = 0x5A; bytes[2] = fill;
        return bytes;
    }

    /// <summary>TestScratch.FlatDir reserves a path and creates its PARENT; the directory
    /// itself is the caller's to create.</summary>
    private static string NewDir(string prefix)
    {
        var dir = TestScratch.FlatDir(prefix);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string R2rChunksRoot(string cacheRoot) => Path.Combine(cacheRoot, "r2r-chunks");

    private static string[] EntryDirs(string cacheRoot)
        => Directory.Exists(R2rChunksRoot(cacheRoot))
            ? Directory.GetDirectories(R2rChunksRoot(cacheRoot)).OrderBy(d => d, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();

    /// <summary>
    /// The persisted cache outlives the process; the content-hash memo does not. Clearing it
    /// between the two halves of an arm is what makes that arm a test of the ON-DISK key
    /// rather than of the in-process memo — the same "simulate a fresh process" step
    /// BcAppSymbolCacheContentAddressedKeyTests takes via ResetProcessCacheForTests.
    /// </summary>
    private static void SimulateProcessRestart() => RunnerFingerprint.ClearFileContentHashMemoForTests();

    private static void WithCacheRoot(Action<string> body)
    {
        var cacheRoot = TestScratch.FlatDir("app-loader-r2r-chunk-identity-tests-");
        CacheRoots.SetOverride(cacheRoot);
        SimulateProcessRestart();
        try { body(cacheRoot); }
        finally
        {
            CacheRoots.ResetForTests();
            SimulateProcessRestart();
            if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true);
        }
    }

    // ── the defect ────────────────────────────────────────────────────────────────

    [Fact]
    public void SamePathSameLengthSameMtime_DifferentBytes_ServesTheNewPackagesChunks()
    {
        WithCacheRoot(cacheRoot =>
        {
            var dir = NewDir("r2r-identity-collide-");
            var appPath = Path.Combine(dir, "Pkg.app");
            var stamp = new DateTime(2021, 6, 1, 12, 0, 0, DateTimeKind.Utc);

            var payloadA = Payload(0xA1);
            WritePackage(appPath, payloadA);
            File.SetLastWriteTimeUtc(appPath, stamp);
            var lengthA = new FileInfo(appPath).Length;

            var pathsA = AppLoader.ExtractAllDllPaths(appPath);
            Assert.Single(pathsA);
            Assert.Equal(payloadA, File.ReadAllBytes(pathsA[0]));
            Assert.Equal(1, AppLoader.R2rExtractInvocationCountForTests(appPath));

            // Same file replaced with different bytes, restored to the same length and the
            // same mtime: `{fullPath}|{Length}|{LastWriteTimeUtc.Ticks}` is now provably
            // identical for two packages with different content.
            SimulateProcessRestart();
            var payloadB = Payload(0xB2);
            Assert.NotEqual(payloadA, payloadB);
            WritePackage(appPath, payloadB);
            File.SetLastWriteTimeUtc(appPath, stamp);
            Assert.Equal(lengthA, new FileInfo(appPath).Length);
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(appPath));

            var pathsB = AppLoader.ExtractAllDllPaths(appPath);
            Assert.Single(pathsB);
            // The decisive assertion: the chunk served describes the package on disk NOW.
            Assert.Equal(payloadB, File.ReadAllBytes(pathsB[0]));
            Assert.NotEqual(payloadA, File.ReadAllBytes(pathsB[0]));
            // ...and it got there by genuinely re-extracting, not by a HIT that happened to
            // hold the right bytes.
            Assert.Equal(2, AppLoader.R2rExtractInvocationCountForTests(appPath));

            // Two contents, two entries — neither overwrote the other's directory.
            Assert.Equal(2, EntryDirs(cacheRoot).Length);
        });
    }

    // ── the arm that must keep passing: an unchanged package is still a HIT ───────

    [Fact]
    public void SamePackageTwice_IsAGenuineHit()
    {
        WithCacheRoot(_ =>
        {
            var dir = NewDir("r2r-identity-hit-");
            var appPath = Path.Combine(dir, "Pkg.app");
            var payload = Payload(0xC3);
            WritePackage(appPath, payload);

            var first = AppLoader.ExtractAllDllPaths(appPath);
            Assert.Single(first);
            Assert.Equal(1, AppLoader.R2rExtractInvocationCountForTests(appPath));

            SimulateProcessRestart();
            var second = AppLoader.ExtractAllDllPaths(appPath);
            Assert.Equal(first, second);
            Assert.Equal(1, AppLoader.R2rExtractInvocationCountForTests(appPath)); // no re-extract
            Assert.Equal(payload, File.ReadAllBytes(second[0]));
        });
    }

    // ── the miss-rate direction (#1815): identical bytes, moved and re-stamped ────

    [Fact]
    public void ByteIdenticalPackageAtAnotherPathWithAnotherMtime_HitsTheSameEntry()
    {
        WithCacheRoot(cacheRoot =>
        {
            var dir = NewDir("r2r-identity-redownload-");
            var pathOne = Path.Combine(dir, "Pkg.app");
            var pathTwo = Path.Combine(dir, "redownloaded", "Pkg.app");
            Directory.CreateDirectory(Path.GetDirectoryName(pathTwo)!);

            var payload = Payload(0xD4);
            WritePackage(pathOne, payload);
            File.SetLastWriteTimeUtc(pathOne, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            var first = AppLoader.ExtractAllDllPaths(pathOne);
            Assert.Single(first);
            Assert.Equal(1, AppLoader.R2rExtractInvocationCountForTests(pathOne));

            // A CI re-download: byte-for-byte the same package, a different path, a fresh
            // mtime. Nothing about its CONTENT changed, so nothing needs re-extracting.
            SimulateProcessRestart();
            File.Copy(pathOne, pathTwo);
            File.SetLastWriteTimeUtc(pathTwo, DateTime.UtcNow);
            Assert.NotEqual(File.GetLastWriteTimeUtc(pathOne), File.GetLastWriteTimeUtc(pathTwo));

            var second = AppLoader.ExtractAllDllPaths(pathTwo);
            Assert.Single(second);
            Assert.Equal(0, AppLoader.R2rExtractInvocationCountForTests(pathTwo)); // served, not extracted
            Assert.Equal(payload, File.ReadAllBytes(second[0]));
            Assert.Equal(first[0], second[0]);       // literally the same entry on disk
            Assert.Single(EntryDirs(cacheRoot));     // one content, one directory
        });
    }

    // ── the guard: no identity ⇒ no shared entry, in either direction ─────────────
    //
    // Driven through ExtractAllDllPathsCore's hash-provider seam. Without the guard the
    // sentinel ("unknown", what RunnerFingerprint.ComputeContentHash answers for a file it
    // cannot read) would BE the directory name, and every unidentifiable package would share
    // one entry — the same wrong-answer shape this issue is about, reintroduced by the fix.

    [Fact]
    public void UnknownContentHash_TwoPackagesNeverShareAnEntry_AndNothingIsPublished()
    {
        WithCacheRoot(cacheRoot =>
        {
            var dir = NewDir("r2r-identity-unknown-");
            var pathA = Path.Combine(dir, "A.app");
            var pathB = Path.Combine(dir, "B.app");
            var payloadA = Payload(0xE5);
            var payloadB = Payload(0xF6);
            WritePackage(pathA, payloadA);
            WritePackage(pathB, payloadB);

            var resultA = AppLoader.ExtractAllDllPathsCore(pathA, static _ => "unknown");
            var resultB = AppLoader.ExtractAllDllPathsCore(pathB, static _ => "unknown");

            Assert.Single(resultA);
            Assert.Single(resultB);
            Assert.Equal(payloadA, File.ReadAllBytes(resultA[0]));
            Assert.Equal(payloadB, File.ReadAllBytes(resultB[0]));
            Assert.NotEqual(resultA[0], resultB[0]);
            // Nothing an unidentifiable package produced may be published where another
            // process could read it as that package's chunks.
            Assert.Empty(EntryDirs(cacheRoot));
        });
    }

    [Fact]
    public void HashProviderThrows_StillServesThisPackagesOwnChunks_AndPublishesNothing()
    {
        WithCacheRoot(cacheRoot =>
        {
            var dir = NewDir("r2r-identity-throw-");
            var appPath = Path.Combine(dir, "Pkg.app");
            var payload = Payload(0x77);
            WritePackage(appPath, payload);

            var result = AppLoader.ExtractAllDllPathsCore(
                appPath, static _ => throw new IOException("cannot read the package"));

            Assert.Single(result);
            Assert.Equal(payload, File.ReadAllBytes(result[0]));
            Assert.Empty(EntryDirs(cacheRoot));
        });
    }

    // ── unchanged contract: a package carrying no R2R chunks ─────────────────────

    [Fact]
    public void PackageWithoutPublishedArtifacts_ReturnsEmpty_AndPublishesNothing()
    {
        WithCacheRoot(cacheRoot =>
        {
            var dir = NewDir("r2r-identity-nonr2r-");
            var appPath = Path.Combine(dir, "NoChunks.app");
            using (var fs = new FileStream(appPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
            using (var s = zip.CreateEntry("SymbolReference.json", CompressionLevel.NoCompression).Open())
            {
                s.Write("{}"u8);
            }

            Assert.Empty(AppLoader.ExtractAllDllPaths(appPath));
            Assert.Empty(EntryDirs(cacheRoot));
        });
    }
}
