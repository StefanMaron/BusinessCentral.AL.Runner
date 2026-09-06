// AppLoaderManifestIndexIdentityTests — issue #2987.
//
// AppLoader.ReadManifest and AppLoader.ReadPackageMeta keyed their PERSISTED, CROSS-PROCESS
// `app-manifests` index on `{fullPath}|{Length}|{LastWriteTimeUtc.Ticks}` — a filesystem stat
// standing in for the package's content, the same substitution #1820 removed from
// BcAppSymbolCache.Get, #2754/#2847 from the AL-output key's dependency terms, #2846 from
// ComputeSourceWorkspaceKey and #2955 from the r2r-chunks cache.
//
// What that index holds is a package's IDENTITY — Publisher, Name, Version, AppId, the whole
// declared Dependencies list — plus HasSymbolReference. So two runs agreeing on the stat and
// disagreeing on the bytes make the second one resolve a package under another package's
// identity, or against another package's declared closure. HasSymbolReference has its own
// documented consequence in ReadPackageMetaFromZip's comment: reporting false for a package
// that carries one "drops a package from the scan set and produces AL1023 against the whole
// compilation".
//
// The pre-#2987 doc comment called the stat "not a correctness hazard, only a cache miss" and
// cited AppLoaderManifestCacheTests.ReadManifest_TouchedMtime_IsReparsedNotServedStale. That
// test covers only the case where the stat MOVES. The hazard is the case where it does not —
// a checkout, a rebuild landing on the same size, a copy preserving mtime — which is what the
// first two arms below construct.
//
// Packages are synthesized rather than taken from a provisioned platform dir, so these arms run
// everywhere: a stat collision has to be CONSTRUCTED (identical length, identical mtime,
// different bytes) and a real .app cannot be edited into that shape.
using System.IO.Compression;
using System.Text;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

// AppLoader's memos and the CacheRoots override are both process-wide mutable static state —
// see CacheRootsSerialCollection's header.
[Collection(CacheRootsSerialCollection.Name)]
public sealed class AppLoaderManifestIndexIdentityTests
{
    // ── package synthesis ─────────────────────────────────────────────────────────

    /// <summary>
    /// A minimal NAVX <c>.app</c>: the 8-byte NAVX header, then a ZIP carrying
    /// <c>NavxManifest.xml</c>, optionally <c>SymbolReference.json</c>, and a STORED
    /// <c>pad.bin</c> of exactly <paramref name="padBytes"/> bytes.
    ///
    /// <para>The pad is what makes a stat collision constructible. Everything is stored
    /// uncompressed and every zip entry's name is fixed, so the file's total length is a known
    /// constant plus <paramref name="padBytes"/> — which lets <see cref="WriteCollidingPair"/>
    /// bring two packages with different content to byte-identical LENGTH without touching
    /// either one's manifest.</para>
    /// </summary>
    private static byte[] BuildApp(
        Guid appId, string name, string publisher, string version,
        Guid depId, string depName, bool symbolReference, int padBytes)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{appId}" Name="{name}" Publisher="{publisher}" Version="{version}"
                   Application="1.0.0.0" Platform="1.0.0.0"/>
              <Dependencies>
                <Dependency Id="{depId}" Name="{depName}" Publisher="DepPub" MinVersion="1.0.0.0"/>
              </Dependencies>
            </Package>
            """;

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var s = zip.CreateEntry("NavxManifest.xml", CompressionLevel.NoCompression).Open())
                s.Write(Encoding.UTF8.GetBytes(xml));
            if (symbolReference)
                using (var s = zip.CreateEntry("SymbolReference.json", CompressionLevel.NoCompression).Open())
                    s.Write("{}"u8);
            if (padBytes > 0)
                using (var s = zip.CreateEntry("pad.bin", CompressionLevel.NoCompression).Open())
                    s.Write(new byte[padBytes]);
        }
        var zipBytes = ms.ToArray();

        var result = new byte[8 + zipBytes.Length];
        result[0] = (byte)'N'; result[1] = (byte)'A'; result[2] = (byte)'V'; result[3] = (byte)'X';
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), (uint)8);
        zipBytes.CopyTo(result, 8);
        return result;
    }

    /// <summary>
    /// Two packages with genuinely different content, padded to exactly the same LENGTH. The
    /// caller writes them to the same path in turn under the same mtime, which is the stat
    /// collision under test.
    /// </summary>
    private static (byte[] A, byte[] B) WriteCollidingPair(
        Guid appIdA, string nameA, string versionA, bool symbolReferenceA,
        Guid appIdB, string nameB, string versionB, bool symbolReferenceB)
    {
        var depId = new Guid("11111111-1111-1111-1111-111111111111");
        byte[] Build(Guid id, string n, string v, bool sym, int pad)
            => BuildApp(id, n, "Pub", v, depId, "Dep", sym, pad);

        // Both start WITH a pad entry, so the entry's own fixed zip overhead (local header +
        // central-directory record for the name "pad.bin") is already in both lengths. From
        // there the payload grows the file 1:1, so one adjustment lands it exactly — padding
        // up from zero would not, because the first pad byte also buys ~90 bytes of headers.
        var a = Build(appIdA, nameA, versionA, symbolReferenceA, 1);
        var b = Build(appIdB, nameB, versionB, symbolReferenceB, 1);
        if (a.Length < b.Length) a = Build(appIdA, nameA, versionA, symbolReferenceA, 1 + (b.Length - a.Length));
        else if (b.Length < a.Length) b = Build(appIdB, nameB, versionB, symbolReferenceB, 1 + (a.Length - b.Length));

        Assert.Equal(a.Length, b.Length);
        Assert.NotEqual(a, b);
        return (a, b);
    }

    // ── harness ───────────────────────────────────────────────────────────────────

    private static string NewDir(string prefix)
    {
        var dir = TestScratch.FlatDir(prefix);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string IndexRoot(string cacheRoot) => Path.Combine(cacheRoot, "app-manifests");

    private static string[] IndexEntries(string cacheRoot)
        => Directory.Exists(IndexRoot(cacheRoot))
            ? Directory.GetFiles(IndexRoot(cacheRoot), "*.json").OrderBy(f => f, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();

    /// <summary>
    /// The persisted index outlives the process; the in-process memos and the shared
    /// content-hash memo do not. Clearing all three between the halves of an arm is what makes
    /// that arm a test of the ON-DISK key rather than of a memo — the same "simulate a fresh
    /// process" step AppLoaderR2rChunkCacheIdentityTests takes.
    /// </summary>
    private static void SimulateProcessRestart()
    {
        AppLoader.ResetManifestMemoForTests();
        RunnerFingerprint.ClearFileContentHashMemoForTests();
    }

    private static void WithCacheRoot(Action<string> body)
    {
        var cacheRoot = TestScratch.FlatDir("app-loader-manifest-index-identity-tests-");
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
    public void ReadManifest_SamePathSameLengthSameMtime_DifferentBytes_ServesTheNewPackagesIdentity()
    {
        WithCacheRoot(cacheRoot =>
        {
            var dir = NewDir("manifest-identity-collide-");
            var appPath = Path.Combine(dir, "Pkg.app");
            var stamp = new DateTime(2021, 6, 1, 12, 0, 0, DateTimeKind.Utc);

            var appIdA = new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var appIdB = new Guid("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
            var (bytesA, bytesB) = WriteCollidingPair(
                appIdA, "AAA", "1.0.0.0", symbolReferenceA: true,
                appIdB, "BBB", "2.0.0.0", symbolReferenceB: true);

            File.WriteAllBytes(appPath, bytesA);
            File.SetLastWriteTimeUtc(appPath, stamp);
            var lengthA = new FileInfo(appPath).Length;

            var manifestA = AppLoader.ReadManifest(appPath);
            Assert.NotNull(manifestA);
            Assert.Equal(appIdA, manifestA!.AppId);
            Assert.Equal("AAA", manifestA.Name);
            Assert.Equal(new Version(1, 0, 0, 0), manifestA.Version);
            Assert.Equal(1, AppLoader.ManifestParseInvocationCountForTests(appPath));

            // Same path replaced with different bytes, restored to the same length and the
            // same mtime: `{fullPath}|{Length}|{LastWriteTimeUtc.Ticks}` is now PROVABLY
            // identical for two packages with different content. Both halves of that claim
            // are asserted before the read that depends on them.
            SimulateProcessRestart();
            File.WriteAllBytes(appPath, bytesB);
            File.SetLastWriteTimeUtc(appPath, stamp);
            Assert.Equal(lengthA, new FileInfo(appPath).Length);
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(appPath));

            var manifestB = AppLoader.ReadManifest(appPath);
            Assert.NotNull(manifestB);
            // The decisive assertions: the identity served describes the package on disk NOW.
            Assert.Equal(appIdB, manifestB!.AppId);
            Assert.Equal("BBB", manifestB.Name);
            Assert.Equal(new Version(2, 0, 0, 0), manifestB.Version);
            Assert.NotEqual(appIdA, manifestB.AppId);
            // ...and it got there by genuinely reparsing, not by a HIT that happened to hold
            // the right answer.
            Assert.Equal(2, AppLoader.ManifestParseInvocationCountForTests(appPath));

            // Two contents, two entries — neither overwrote the other's.
            Assert.Equal(2, IndexEntries(cacheRoot).Length);
        });
    }

    [Fact]
    public void ReadPackageMeta_SamePathSameLengthSameMtime_DifferentSymbolReference_IsNotServedStale()
    {
        WithCacheRoot(_ =>
        {
            var dir = NewDir("manifest-identity-symref-");
            var appPath = Path.Combine(dir, "Pkg.app");
            var stamp = new DateTime(2022, 3, 4, 5, 6, 7, DateTimeKind.Utc);

            var appIdA = new Guid("cccccccc-cccc-cccc-cccc-cccccccccccc");
            var appIdB = new Guid("dddddddd-dddd-dddd-dddd-dddddddddddd");
            // A carries NO SymbolReference.json, B does. Reporting A's `false` for B is the
            // answer that drops a package from the scan set and produces AL1023 against the
            // whole compilation.
            var (bytesA, bytesB) = WriteCollidingPair(
                appIdA, "AAA", "1.0.0.0", symbolReferenceA: false,
                appIdB, "BBB", "1.0.0.0", symbolReferenceB: true);

            File.WriteAllBytes(appPath, bytesA);
            File.SetLastWriteTimeUtc(appPath, stamp);
            var lengthA = new FileInfo(appPath).Length;

            var metaA = AppLoader.ReadPackageMeta(appPath);
            Assert.NotNull(metaA.Manifest);
            Assert.Equal(appIdA, metaA.Manifest!.AppId);
            Assert.False(metaA.HasSymbolReference);

            SimulateProcessRestart();
            File.WriteAllBytes(appPath, bytesB);
            File.SetLastWriteTimeUtc(appPath, stamp);
            Assert.Equal(lengthA, new FileInfo(appPath).Length);
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(appPath));

            var metaB = AppLoader.ReadPackageMeta(appPath);
            Assert.NotNull(metaB.Manifest);
            Assert.Equal(appIdB, metaB.Manifest!.AppId);
            Assert.True(metaB.HasSymbolReference);
            // HasSymbolReference is what AppLoader.HasSymbolReference answers, so assert
            // through the public entry point the scan set actually consults too.
            Assert.True(AppLoader.HasSymbolReference(appPath));
        });
    }

    // ── the arms that must keep passing: the index is still a cache ───────────────

    [Fact]
    public void ReadManifest_SamePackageTwiceAcrossProcesses_IsAGenuineIndexHit()
    {
        WithCacheRoot(_ =>
        {
            var dir = NewDir("manifest-identity-hit-");
            var appPath = Path.Combine(dir, "Pkg.app");
            var appId = new Guid("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
            File.WriteAllBytes(appPath, BuildApp(
                appId, "Hit", "Pub", "3.2.1.0",
                new Guid("22222222-2222-2222-2222-222222222222"), "Dep", symbolReference: true, padBytes: 0));

            var first = AppLoader.ReadManifest(appPath);
            Assert.Equal(appId, first!.AppId);
            Assert.Equal(1, AppLoader.ManifestParseInvocationCountForTests(appPath));

            SimulateProcessRestart();
            var second = AppLoader.ReadManifest(appPath);
            Assert.Equal(appId, second!.AppId);
            Assert.Equal("Hit", second.Name);
            Assert.Equal(new Version(3, 2, 1, 0), second.Version);
            Assert.Single(second.Dependencies);
            // Served from the on-disk index across a simulated process restart, not reparsed.
            Assert.Equal(1, AppLoader.ManifestParseInvocationCountForTests(appPath));
        });
    }

    [Fact]
    public void ReadManifest_ByteIdenticalPackageAtAnotherPathWithAnotherMtime_HitsTheSameEntry()
    {
        WithCacheRoot(cacheRoot =>
        {
            var dir = NewDir("manifest-identity-redownload-");
            var pathOne = Path.Combine(dir, "Pkg.app");
            var pathTwo = Path.Combine(dir, "redownloaded", "Pkg.app");
            Directory.CreateDirectory(Path.GetDirectoryName(pathTwo)!);

            var appId = new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff");
            var bytes = BuildApp(appId, "Redownloaded", "Pub", "1.2.3.4",
                new Guid("33333333-3333-3333-3333-333333333333"), "Dep", symbolReference: true, padBytes: 0);
            File.WriteAllBytes(pathOne, bytes);
            File.SetLastWriteTimeUtc(pathOne, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            Assert.Equal(appId, AppLoader.ReadManifest(pathOne)!.AppId);
            Assert.Equal(1, AppLoader.ManifestParseInvocationCountForTests(pathOne));

            // A CI re-download: byte-for-byte the same package, a different path, a fresh
            // mtime. Nothing about its CONTENT changed, so nothing needs reparsing — the case
            // the stat key MISSed unconditionally (#1815's argument).
            SimulateProcessRestart();
            File.Copy(pathOne, pathTwo);
            File.SetLastWriteTimeUtc(pathTwo, DateTime.UtcNow);
            Assert.NotEqual(File.GetLastWriteTimeUtc(pathOne), File.GetLastWriteTimeUtc(pathTwo));

            var served = AppLoader.ReadManifest(pathTwo);
            Assert.Equal(appId, served!.AppId);
            Assert.Equal("Redownloaded", served.Name);
            Assert.Equal(0, AppLoader.ManifestParseInvocationCountForTests(pathTwo)); // served, not parsed
            Assert.Single(IndexEntries(cacheRoot));                                   // one content, one entry
        });
    }

    // ── the guard: no identity ⇒ no shared entry, in either direction ─────────────
    //
    // Driven through the hash-provider seam. Without the guard the sentinel ("unknown", what
    // RunnerFingerprint.ComputeContentHash answers for a file it cannot read) would BE the
    // entry name, and every unidentifiable package would share one entry — the same
    // wrong-answer shape this fix removes, reintroduced by the fix.

    [Fact]
    public void UnknownContentHash_TwoPackagesNeverShareAnIndexEntry_AndNothingIsPublished()
    {
        WithCacheRoot(cacheRoot =>
        {
            var dir = NewDir("manifest-identity-unknown-");
            var pathA = Path.Combine(dir, "A.app");
            var pathB = Path.Combine(dir, "B.app");
            var appIdA = new Guid("0a0a0a0a-0a0a-0a0a-0a0a-0a0a0a0a0a0a");
            var appIdB = new Guid("0b0b0b0b-0b0b-0b0b-0b0b-0b0b0b0b0b0b");
            var depId = new Guid("44444444-4444-4444-4444-444444444444");
            File.WriteAllBytes(pathA, BuildApp(appIdA, "AAA", "Pub", "1.0.0.0", depId, "Dep", true, 0));
            File.WriteAllBytes(pathB, BuildApp(appIdB, "BBB", "Pub", "1.0.0.0", depId, "Dep", true, 0));

            var a = AppLoader.ReadManifestCore(pathA, static _ => RunnerFingerprint.UnknownContentHash);
            var b = AppLoader.ReadManifestCore(pathB, static _ => RunnerFingerprint.UnknownContentHash);

            // Each package still answers its OWN identity — the guard costs a reparse, never a
            // wrong answer.
            Assert.Equal(appIdA, a!.AppId);
            Assert.Equal(appIdB, b!.AppId);
            Assert.Equal("AAA", a.Name);
            Assert.Equal("BBB", b.Name);
            // Nothing an unidentifiable package produced may be published where another
            // process could read it as that package's manifest.
            Assert.Empty(IndexEntries(cacheRoot));
        });
    }

    [Fact]
    public void HashProviderThrows_StillAnswersThisPackagesOwnIdentity_AndPublishesNothing()
    {
        WithCacheRoot(cacheRoot =>
        {
            var dir = NewDir("manifest-identity-throw-");
            var appPath = Path.Combine(dir, "Pkg.app");
            var appId = new Guid("0c0c0c0c-0c0c-0c0c-0c0c-0c0c0c0c0c0c");
            File.WriteAllBytes(appPath, BuildApp(appId, "Throws", "Pub", "9.9.9.9",
                new Guid("55555555-5555-5555-5555-555555555555"), "Dep", true, 0));

            var manifest = AppLoader.ReadManifestCore(
                appPath, static _ => throw new IOException("cannot read the package"));

            Assert.NotNull(manifest);
            Assert.Equal(appId, manifest!.AppId);
            Assert.Equal("Throws", manifest.Name);
            Assert.Empty(IndexEntries(cacheRoot));
        });
    }

    [Fact]
    public void ReadPackageMeta_UnknownContentHash_PublishesNothing_AndStillAnswersBothQuestions()
    {
        WithCacheRoot(cacheRoot =>
        {
            var dir = NewDir("manifest-identity-meta-unknown-");
            var withSym = Path.Combine(dir, "WithSym.app");
            var withoutSym = Path.Combine(dir, "WithoutSym.app");
            var depId = new Guid("66666666-6666-6666-6666-666666666666");
            var idWith = new Guid("0d0d0d0d-0d0d-0d0d-0d0d-0d0d0d0d0d0d");
            var idWithout = new Guid("0e0e0e0e-0e0e-0e0e-0e0e-0e0e0e0e0e0e");
            File.WriteAllBytes(withSym, BuildApp(idWith, "WithSym", "Pub", "1.0.0.0", depId, "Dep", true, 0));
            File.WriteAllBytes(withoutSym, BuildApp(idWithout, "NoSym", "Pub", "1.0.0.0", depId, "Dep", false, 0));

            var w = AppLoader.ReadPackageMetaCore(withSym, static _ => RunnerFingerprint.UnknownContentHash);
            var n = AppLoader.ReadPackageMetaCore(withoutSym, static _ => RunnerFingerprint.UnknownContentHash);

            Assert.Equal(idWith, w.Manifest!.AppId);
            Assert.True(w.HasSymbolReference);
            Assert.Equal(idWithout, n.Manifest!.AppId);
            Assert.False(n.HasSymbolReference);
            Assert.Empty(IndexEntries(cacheRoot));
        });
    }

    // ── the entry-name convention ────────────────────────────────────────────────

    /// <summary>
    /// A pre-#2987 stat-keyed entry name is also 64 lowercase hex characters plus
    /// <c>.json</c> — indistinguishable from a content hash by shape. The <c>sha256-</c>
    /// prefix is what makes a warm pre-fix cache directory unreadable as a content-keyed one
    /// rather than silently misread (#2955's convention).
    /// </summary>
    [Fact]
    public void IndexEntryName_CarriesTheSha256Prefix()
    {
        WithCacheRoot(cacheRoot =>
        {
            var dir = NewDir("manifest-identity-name-");
            var appPath = Path.Combine(dir, "Pkg.app");
            File.WriteAllBytes(appPath, BuildApp(
                new Guid("0f0f0f0f-0f0f-0f0f-0f0f-0f0f0f0f0f0f"), "Named", "Pub", "1.0.0.0",
                new Guid("77777777-7777-7777-7777-777777777777"), "Dep", true, 0));

            Assert.NotNull(AppLoader.ReadManifest(appPath));

            var entries = IndexEntries(cacheRoot);
            Assert.Single(entries);
            var name = Path.GetFileName(entries[0]);
            Assert.StartsWith("sha256-", name);
            Assert.Equal("sha256-" + RunnerFingerprint.ComputeContentHash(appPath) + ".json", name);
            Assert.Equal(entries[0], AppLoader.ManifestIndexPathForTests(appPath));
        });
    }
}
