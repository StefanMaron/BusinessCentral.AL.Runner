// BcAppSymbolCacheContentAddressedKeyTests — proves the bc-symbols on-disk cache key no
// longer embeds the .app file's mtime (issue #1820, same defect family as #1815/#1817).
//
// Gap being fixed
// ----------------
// BcAppSymbolCache.Get(appPath) used to key the cache as
//     {fullPath}|{FileInfo.Length}|{FileInfo.LastWriteTimeUtc.Ticks}|v{CacheVersion}
// and TryRead re-validated Length/LastWriteUtcTicks against the stored payload before
// serving a HIT — mtime-sensitive twice over. CI re-downloads every platform/test-toolkit
// .app on every run (bc-tests.yml's "Download R2R platform apps" / "Download the
// Microsoft test toolkit" steps), stamping a fresh mtime even when the bytes are
// byte-for-byte identical to what a persisted cache holds — so a bc-symbols entry
// persisted across CI runs would MISS unconditionally, regardless of content.
//
// The fix replaces the Length/LastWriteTimeUtc key components with a SHA-256 content hash
// of the .app's bytes (BcAppSymbolCache.ComputeAppContentHash, reusing
// RunnerFingerprint.ComputeContentHash — the same content-hash-of-bytes helper #1817
// introduced for the AL-output/source-dep caches, rather than inventing a second
// convention).
//
// A test asserting only that the KEY/HASH is now mtime-independent would pass against an
// implementation that still misses on disk (e.g. one that forgot to also fix TryRead's
// payload-level re-validation). The decisive tests below therefore drive BcAppSymbolCache
// .Get() end-to-end across a SIMULATED separate process (BcAppSymbolCache
// .ResetProcessCacheForTests() clears the in-memory ProcessCache the same way a fresh
// process would start empty) and assert, via the internal Parse-invocation counter, that
// the SECOND run genuinely reused the on-disk payload rather than re-parsing.
using System.IO.Compression;
using System.Text;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// #1821: BcAppSymbolCache.Get() now resolves its on-disk path through the process-global
// CacheRoots override, so this joins CacheRootsSerialCollection to avoid racing
// CacheRootsTests's SetOverride calls — see that collection's header for why.
[Collection(CacheRootsSerialCollection.Name)]
public sealed class BcAppSymbolCacheContentAddressedKeyTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bc-symbol-cache-content-key-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Minimal-but-valid .app shape: a plain zip holding SymbolReference.json, mirroring
    // BcAppSymbolCacheReportTests / BcAppSymbolCachePageMetadataTests. The Guid in the
    // object name keeps each test's content genuinely unique so cache entries from
    // different test runs (and different xunit test methods sharing the same real
    // ~/.cache/al-runner/bc-symbols directory) can never collide.
    private static string WriteApp(string dir, string fileName, string tableName)
    {
        var appPath = Path.Combine(dir, fileName);
        using (var zip = new FileStream(appPath, FileMode.Create))
        using (var za = new ZipArchive(zip, ZipArchiveMode.Create))
        {
            var entry = za.CreateEntry("SymbolReference.json");
            using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
            w.Write($$"""
                {
                  "RuntimeVersion": "15.1",
                  "Tables": [
                    { "Id": 50100, "Name": "{{tableName}}", "Fields": [], "Keys": [] }
                  ]
                }
                """);
        }
        return appPath;
    }

    /// <summary>
    /// Unit-level key-computation proof (mirrors RunnerFingerprintTests' shape): two files
    /// with byte-identical content but DIFFERENT mtimes must hash to the SAME content hash
    /// — the actual bug. A key built from FileInfo.LastWriteTimeUtc would differ here.
    /// </summary>
    [Fact]
    public void ComputeAppContentHash_SameBytesDifferentMtime_ProducesEqualHash()
    {
        var dir = NewTempDir();
        try
        {
            var pathA = Path.Combine(dir, "app-a.app");
            var pathB = Path.Combine(dir, "app-b.app");
            var bytes = new byte[] { (byte)'P', (byte)'K', 3, 4, 1, 2, 3, 4, 5, 6, 7, 8 }; // fake "zip-ish" bytes
            File.WriteAllBytes(pathA, bytes);
            File.WriteAllBytes(pathB, bytes);

            File.SetLastWriteTimeUtc(pathA, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(pathB, new DateTime(2026, 6, 15, 12, 34, 56, DateTimeKind.Utc));
            Assert.NotEqual(File.GetLastWriteTimeUtc(pathA), File.GetLastWriteTimeUtc(pathB));

            var hashA = BcAppSymbolCache.ComputeAppContentHash(pathA);
            var hashB = BcAppSymbolCache.ComputeAppContentHash(pathB);

            Assert.Equal(hashA, hashB);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Negative companion: different .app CONTENT must still yield different
    /// hashes — content-addressing must not degenerate into a constant.</summary>
    [Fact]
    public void ComputeAppContentHash_DifferentBytes_ProducesDifferentHash()
    {
        var dir = NewTempDir();
        try
        {
            var pathA = Path.Combine(dir, "app-a.app");
            var pathB = Path.Combine(dir, "app-b.app");
            File.WriteAllBytes(pathA, new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(pathB, new byte[] { 1, 2, 3, 5 });

            var hashA = BcAppSymbolCache.ComputeAppContentHash(pathA);
            var hashB = BcAppSymbolCache.ComputeAppContentHash(pathB);

            Assert.NotEqual(hashA, hashB);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// THE decisive positive test: two Get() calls for the SAME .app content, across a
    /// simulated separate process (ProcessCache cleared) and with the .app's mtime touched
    /// to a materially different value in between, must produce equal results AND the
    /// second call must NOT re-invoke Parse — i.e. a genuine on-disk HIT, not a MISS that
    /// happens to reparse to the same values. This is the actual CI scenario: a fresh
    /// checkout re-downloads the .app (new mtime) but the bytes are identical to what a
    /// persisted bc-symbols cache holds from a prior run.
    /// </summary>
    [Fact]
    public void Get_SameContentDifferentMtimeAcrossSimulatedProcesses_IsADiskHitNotAReparse()
    {
        var dir = NewTempDir();
        try
        {
            var uniqueTable = "ContentKeyHitTest_" + Guid.NewGuid().ToString("N");
            var appPath = WriteApp(dir, "hit-" + Guid.NewGuid().ToString("N") + ".app", uniqueTable);

            BcAppSymbolCache.ResetProcessCacheForTests();
            Assert.Equal(0, BcAppSymbolCache.ParseInvocationCountForTests(appPath));

            var first = BcAppSymbolCache.Get(appPath);
            Assert.Contains(first.Tables, t => t.TableName == uniqueTable);
            Assert.Equal(1, BcAppSymbolCache.ParseInvocationCountForTests(appPath));

            // Simulate "next CI run": fresh process (ProcessCache starts empty) with a
            // materially different mtime on the SAME bytes — exactly what re-downloading
            // an identical .app produces.
            BcAppSymbolCache.ResetProcessCacheForTests();
            File.SetLastWriteTimeUtc(appPath, DateTime.UtcNow.AddDays(-30));

            var second = BcAppSymbolCache.Get(appPath);

            Assert.Contains(second.Tables, t => t.TableName == uniqueTable);
            // The decisive assertion: Parse was NOT invoked again. If the key (or TryRead's
            // payload validation) still depended on mtime, this call would MISS and the
            // per-path count would read 2.
            Assert.Equal(1, BcAppSymbolCache.ParseInvocationCountForTests(appPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// Negative companion: the fix must not make the cache HIT unconditionally regardless
    /// of content. A genuinely DIFFERENT .app at the same path (content changed) must still
    /// MISS and reparse, even across the same simulated-process reset.
    /// </summary>
    [Fact]
    public void Get_DifferentContentAtSamePathAcrossSimulatedProcesses_StillReparses()
    {
        var dir = NewTempDir();
        try
        {
            var appPath = Path.Combine(dir, "mutating-" + Guid.NewGuid().ToString("N") + ".app");
            var tableV1 = "ContentKeyMissTestV1_" + Guid.NewGuid().ToString("N");
            var tableV2 = "ContentKeyMissTestV2_" + Guid.NewGuid().ToString("N");

            WriteApp(dir, Path.GetFileName(appPath), tableV1);
            BcAppSymbolCache.ResetProcessCacheForTests();
            Assert.Equal(0, BcAppSymbolCache.ParseInvocationCountForTests(appPath));

            var first = BcAppSymbolCache.Get(appPath);
            Assert.Contains(first.Tables, t => t.TableName == tableV1);
            Assert.Equal(1, BcAppSymbolCache.ParseInvocationCountForTests(appPath));

            // Overwrite the SAME path with genuinely different content (same mtime
            // resolution risk as the positive test doesn't matter here — content changed).
            File.Delete(appPath);
            WriteApp(dir, Path.GetFileName(appPath), tableV2);

            BcAppSymbolCache.ResetProcessCacheForTests();
            var second = BcAppSymbolCache.Get(appPath);

            Assert.Contains(second.Tables, t => t.TableName == tableV2);
            Assert.DoesNotContain(second.Tables, t => t.TableName == tableV1);
            // Content genuinely changed -> must reparse, not silently serve the stale HIT.
            Assert.Equal(2, BcAppSymbolCache.ParseInvocationCountForTests(appPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
