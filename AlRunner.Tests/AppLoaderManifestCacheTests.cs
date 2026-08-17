// AppLoaderManifestCacheTests — proves AppLoader.ReadManifest's two-level cache (issue
// #perf-B): an in-process memo, and — on a memo miss — a small on-disk index under
// CacheRoots.Resolve("app-manifests"), both keyed by (full path, length, last-write-time
// UTC) rather than a content hash (hashing a 98MB Base Application .app just to answer
// "have I seen this file before" would reintroduce the exact cost this cache exists to
// avoid).
//
// Decisive proof, not just "returns something": every positive assertion below is paired
// with AppLoader.ManifestParseInvocationCountForTests(appPath), the same
// "did a REAL parse happen" counter pattern BcAppSymbolCacheContentAddressedKeyTests uses
// for BcAppSymbolCache. A test asserting only that the returned manifest looks right would
// pass against an implementation that quietly reparses every time (correct but not the
// perf fix); the counter is what tells "served from cache" apart from "reparsed to the
// same answer".
using System.IO.Compression;
using System.Text;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

// AppLoader's manifest memo + CacheRoots override are both process-wide mutable static
// state — joins CacheRootsSerialCollection alongside CacheRootsTests/BcAppSymbolCache*Tests
// so this class's CacheRoots.SetOverride calls never race another collection's (see that
// collection's header for the full rationale).
[Collection(CacheRootsSerialCollection.Name)]
public sealed class AppLoaderManifestCacheTests
{
    private static string NewTempDir(string suffix)
    {
        var dir = Path.Combine(Path.GetTempPath(), "app-loader-manifest-cache-tests-" + suffix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Writes a minimal NAVX .app (header + ZIP with NavxManifest.xml), including
    /// a dependency and Application/Platform attributes so every AppManifest field the
    /// on-disk payload must round-trip is actually exercised.</summary>
    private static string WriteApp(
        string dir, string fileName, Guid appId, string name, string publisher, string version,
        Guid depId, string depName, string depPublisher, string depVersion,
        string application, string platform)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{appId}" Name="{name}" Publisher="{publisher}" Version="{version}"
                   Application="{application}" Platform="{platform}"/>
              <Dependencies>
                <Dependency Id="{depId}" Name="{depName}" Publisher="{depPublisher}" MinVersion="{depVersion}"/>
              </Dependencies>
            </Package>
            """;

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("NavxManifest.xml");
            using var es = entry.Open();
            es.Write(Encoding.UTF8.GetBytes(xml));
        }
        var zipBytes = ms.ToArray();

        // NAVX wrapper: magic "NAVX" + LE uint32 ZIP offset (8) + ZIP bytes.
        var result = new byte[8 + zipBytes.Length];
        result[0] = (byte)'N'; result[1] = (byte)'A'; result[2] = (byte)'V'; result[3] = (byte)'X';
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), (uint)8);
        zipBytes.CopyTo(result, 8);

        var appPath = Path.Combine(dir, fileName);
        File.WriteAllBytes(appPath, result);
        return appPath;
    }

    /// <summary>Deep field-by-field comparison — AppManifest is a record, but its
    /// Dependencies list does NOT get structural equality from the record's own
    /// auto-generated Equals (List&lt;T&gt; has reference equality), so a plain
    /// Assert.Equal(a, b) would not actually prove the dependency list round-tripped.</summary>
    private static void AssertManifestEqual(AppManifest expected, AppManifest actual)
    {
        Assert.Equal(expected.Publisher, actual.Publisher);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.AppId, actual.AppId);
        Assert.Equal(expected.Application, actual.Application);
        Assert.Equal(expected.Platform, actual.Platform);
        Assert.Equal(expected.Dependencies.Count, actual.Dependencies.Count);
        for (int i = 0; i < expected.Dependencies.Count; i++)
        {
            Assert.Equal(expected.Dependencies[i].AppId, actual.Dependencies[i].AppId);
            Assert.Equal(expected.Dependencies[i].Name, actual.Dependencies[i].Name);
            Assert.Equal(expected.Dependencies[i].Publisher, actual.Dependencies[i].Publisher);
            Assert.Equal(expected.Dependencies[i].Version, actual.Dependencies[i].Version);
            Assert.Equal(expected.Dependencies[i].Optional, actual.Dependencies[i].Optional);
        }
    }

    /// <summary>
    /// THE decisive positive test: three ReadManifest() calls for the SAME file — a cold
    /// call (memo empty, disk empty: a genuine parse), a call with only the in-process memo
    /// cleared (must be served from the on-disk index), and a call with nothing cleared
    /// (must be served from the in-process memo) — all return content that agrees on every
    /// field, and only the FIRST call is a genuine parse.
    /// </summary>
    [Fact]
    public void ReadManifest_MemoDiskAndFreshParse_AllProduceIdenticalContentAndOnlyOneRealParse()
    {
        var cacheRoot = NewTempDir("cache");
        var srcDir = NewTempDir("src");
        CacheRoots.SetOverride(cacheRoot);
        AppLoader.ResetManifestMemoForTests();
        try
        {
            var appId = Guid.NewGuid();
            var depId = Guid.NewGuid();
            var appPath = WriteApp(srcDir, "app.app", appId, "My App", "My Publisher", "1.2.3.4",
                depId, "My Dep", "Dep Publisher", "5.6.7.8", "24.0.0.0", "25.0.0.0");

            Assert.Equal(0, AppLoader.ManifestParseInvocationCountForTests(appPath));

            // 1. Cold: memo empty, disk empty -> genuine parse.
            var fresh = AppLoader.ReadManifest(appPath);
            Assert.NotNull(fresh);
            Assert.Equal(1, AppLoader.ManifestParseInvocationCountForTests(appPath));
            Assert.Equal("My App", fresh!.Name);
            Assert.Equal("My Publisher", fresh.Publisher);
            Assert.Equal(new Version(1, 2, 3, 4), fresh.Version);
            Assert.Equal(appId, fresh.AppId);
            Assert.Equal(new Version(24, 0, 0, 0), fresh.Application);
            Assert.Equal(new Version(25, 0, 0, 0), fresh.Platform);
            Assert.Single(fresh.Dependencies);
            Assert.Equal(depId, fresh.Dependencies[0].AppId);
            Assert.Equal("My Dep", fresh.Dependencies[0].Name);
            Assert.Equal("Dep Publisher", fresh.Dependencies[0].Publisher);
            Assert.Equal(new Version(5, 6, 7, 8), fresh.Dependencies[0].Version);

            // The on-disk index must now exist for this exact key.
            var indexPath = AppLoader.ManifestIndexPathForTests(appPath);
            Assert.True(File.Exists(indexPath));

            // 2. Clear ONLY the in-process memo -> must be served from the on-disk index,
            //    not a second parse.
            AppLoader.ResetManifestMemoForTests();
            var fromDisk = AppLoader.ReadManifest(appPath);
            Assert.NotNull(fromDisk);
            Assert.Equal(1, AppLoader.ManifestParseInvocationCountForTests(appPath)); // still 1 — no reparse
            AssertManifestEqual(fresh, fromDisk!);

            // 3. Call again with NOTHING cleared -> must be served from the in-process memo.
            var fromMemo = AppLoader.ReadManifest(appPath);
            Assert.NotNull(fromMemo);
            Assert.Equal(1, AppLoader.ManifestParseInvocationCountForTests(appPath)); // still 1
            AssertManifestEqual(fresh, fromMemo!);
        }
        finally
        {
            CacheRoots.ResetForTests();
            AppLoader.ResetManifestMemoForTests();
            Directory.Delete(cacheRoot, recursive: true);
            Directory.Delete(srcDir, recursive: true);
        }
    }

    /// <summary>
    /// A file touched to a materially different mtime (or size) at the SAME path must be
    /// reparsed, never served the stale cached content — even though nothing about the
    /// PATH changed. This is what "keyed by length+mtime, not just path" buys: a package
    /// re-downloaded/rebuilt in place is picked up, not silently ignored.
    /// </summary>
    [Fact]
    public void ReadManifest_TouchedMtime_IsReparsedNotServedStale()
    {
        var cacheRoot = NewTempDir("cache");
        var srcDir = NewTempDir("src");
        CacheRoots.SetOverride(cacheRoot);
        AppLoader.ResetManifestMemoForTests();
        try
        {
            var appId = Guid.NewGuid();
            var depId = Guid.NewGuid();
            var appPath = WriteApp(srcDir, "app.app", appId, "V1 Name", "Pub", "1.0.0.0",
                depId, "Dep", "DepPub", "1.0.0.0", "1.0.0.0", "1.0.0.0");

            var v1 = AppLoader.ReadManifest(appPath);
            Assert.Equal("V1 Name", v1!.Name);
            Assert.Equal(1, AppLoader.ManifestParseInvocationCountForTests(appPath));

            // Overwrite the SAME path with genuinely different content, then force the
            // mtime to something materially different (WriteApp's own File.WriteAllBytes
            // already changes mtime on most filesystems, but pin it explicitly so the test
            // is not filesystem-timestamp-resolution-dependent).
            var newAppId = Guid.NewGuid();
            WriteApp(srcDir, "app.app", newAppId, "V2 Name", "Pub", "2.0.0.0",
                depId, "Dep", "DepPub", "1.0.0.0", "1.0.0.0", "1.0.0.0");
            File.SetLastWriteTimeUtc(appPath, DateTime.UtcNow.AddDays(1));

            // Even with the in-process memo still warm (NOT reset), the new stat must miss
            // the old memo key and reparse.
            var v2 = AppLoader.ReadManifest(appPath);
            Assert.NotNull(v2);
            Assert.Equal("V2 Name", v2!.Name);
            Assert.Equal(newAppId, v2.AppId);
            Assert.Equal(2, AppLoader.ManifestParseInvocationCountForTests(appPath)); // reparsed, not stale
        }
        finally
        {
            CacheRoots.ResetForTests();
            AppLoader.ResetManifestMemoForTests();
            Directory.Delete(cacheRoot, recursive: true);
            Directory.Delete(srcDir, recursive: true);
        }
    }

    /// <summary>
    /// A corrupt/unreadable on-disk index entry must never propagate a wrong answer or
    /// throw — it is silently (well, verbose-logged) ignored, and the caller falls through
    /// to a fresh, correct reparse. Loud-failures.md's "a cache that falls back to
    /// recomputing on miss/corruption is fine" clause, applied to this cache.
    /// </summary>
    [Fact]
    public void ReadManifest_CorruptIndexEntry_IsIgnoredAndReparsed()
    {
        var cacheRoot = NewTempDir("cache");
        var srcDir = NewTempDir("src");
        CacheRoots.SetOverride(cacheRoot);
        AppLoader.ResetManifestMemoForTests();
        try
        {
            var appId = Guid.NewGuid();
            var depId = Guid.NewGuid();
            var appPath = WriteApp(srcDir, "app.app", appId, "Real Name", "Pub", "3.0.0.0",
                depId, "Dep", "DepPub", "1.0.0.0", "1.0.0.0", "1.0.0.0");

            var first = AppLoader.ReadManifest(appPath);
            Assert.Equal("Real Name", first!.Name);
            Assert.Equal(1, AppLoader.ManifestParseInvocationCountForTests(appPath));

            // Corrupt the exact on-disk index entry this file's current stat maps to.
            var indexPath = AppLoader.ManifestIndexPathForTests(appPath);
            Assert.True(File.Exists(indexPath));
            File.WriteAllText(indexPath, "{ not valid json this is garbage !!! ");

            // Memo cleared so the corrupted disk entry is actually consulted, not skipped.
            AppLoader.ResetManifestMemoForTests();
            var second = AppLoader.ReadManifest(appPath);

            Assert.NotNull(second);
            Assert.Equal("Real Name", second!.Name);
            Assert.Equal(appId, second.AppId);
            // The corrupt entry forced a genuine reparse — never a null, never a throw, never
            // a silently wrong answer.
            Assert.Equal(2, AppLoader.ManifestParseInvocationCountForTests(appPath));

            // And the reparse must have repaired the index (self-healing): a THIRD call with
            // the memo cleared again should now hit the disk index without reparsing.
            AppLoader.ResetManifestMemoForTests();
            var third = AppLoader.ReadManifest(appPath);
            Assert.NotNull(third);
            Assert.Equal(2, AppLoader.ManifestParseInvocationCountForTests(appPath)); // no third reparse
        }
        finally
        {
            CacheRoots.ResetForTests();
            AppLoader.ResetManifestMemoForTests();
            Directory.Delete(cacheRoot, recursive: true);
            Directory.Delete(srcDir, recursive: true);
        }
    }

    /// <summary>
    /// Negative control on ReadManifest's existing (pre-cache) contract: a missing file
    /// still returns null, never throws — the memo/index machinery must not change this.
    /// </summary>
    [Fact]
    public void ReadManifest_MissingFile_ReturnsNullNotThrow()
    {
        var cacheRoot = NewTempDir("cache");
        CacheRoots.SetOverride(cacheRoot);
        AppLoader.ResetManifestMemoForTests();
        try
        {
            var missingPath = Path.Combine(Path.GetTempPath(), "app-loader-manifest-cache-tests-missing-" + Guid.NewGuid().ToString("N") + ".app");
            var result = AppLoader.ReadManifest(missingPath);
            Assert.Null(result);
        }
        finally
        {
            CacheRoots.ResetForTests();
            AppLoader.ResetManifestMemoForTests();
            Directory.Delete(cacheRoot, recursive: true);
        }
    }
}
