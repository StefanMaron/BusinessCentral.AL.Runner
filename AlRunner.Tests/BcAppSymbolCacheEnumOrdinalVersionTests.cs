// BcAppSymbolCacheEnumOrdinalVersionTests — proves the CacheVersion bump for #3805's
// enum-ordinal parse change actually invalidates a stale on-disk entry, instead of
// silently replaying the bug the change was made to fix.
//
// Gap being fixed
// ----------------
// TryParseEnumSymbol used to read a value stating no `Ordinal` as "the previous value's
// ordinal plus one"; SymbolReference.json omits the property exactly when the ordinal is
// ZERO, so the fallback is the constant 0 (#3805).
//
// That change alters the PARSE, not the record shape: EnumSymbol.Indexes is a List<int>
// either way. The cache key is `{fullPath}|hash:{contentHash}|v{CacheVersion}|shape:
// {PayloadShape}` (BcAppSymbolCache.BuildKey), so with the .app's bytes unchanged, `path`,
// `hash` and `shape` are all identical before and after — leaving CacheVersion as the ONLY
// discriminator. Without the bump, a machine whose cache was written by the previous build
// keeps matching the same key and replays the OLD ordinals forever: the fix ships fully
// deployed and does nothing on any warm box.
//
// This is the trap the constant's own comment block records five times over, and v36 is
// this very method (#3594, TryParseEnumSymbol, shape unchanged).
//
// Why the wrong answer is worse than a wrong number: the old rule gives System Application
// 2616 "Printer Paper Kind" a DUPLICATE ordinal — Custom and GermanStandardFanfold both at
// 40 — and AlEnumMetadataRegistry.TryGet's merge dedupes on ordinal, so the collision drops
// a value rather than merely mis-numbering it.
//
// Test strategy
// -------------
// A test that writes and reads back a FRESH entry cannot catch a missing version bump — a
// fresh entry always carries the new ordinals, whichever number the key embeds. So this
// plants the payload a PREVIOUS-version build would have written (the real .app's content
// hash, but Indexes holding the old rule's answer) at the exact previous-version-keyed
// path, then calls Get() with the current code and asserts both that the correct ordinal
// survives AND that the .app was genuinely reparsed rather than served as a HIT.
//
// The stale path is located through BcAppSymbolCache.CachePathForVersionForTests, the seam
// that delegates to the same private CachePath formula Get() uses, never a copy of it —
// see BcAppSymbolCacheQueryMethodVersionTests' header for why a copy would pass for the
// wrong reason.
//
// One trap worth naming, because it silently invalidates this measurement: a zip embeds an
// mtime, so REWRITING the .app between the two reads moves the content hash and therefore
// the whole key — the run then shows the fixed value and looks exactly like "no cache
// problem". The .app here is written once.
using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// #1821: Get() resolves its on-disk path through the process-global CacheRoots override,
// so this joins CacheRootsSerialCollection for the same reason the sibling cache tests do.
[Collection(CacheRootsSerialCollection.Name)]
public sealed class BcAppSymbolCacheEnumOrdinalVersionTests
{
    // The shape that discriminates the two rules, reduced from System Application 2616:
    // values NOT in ordinal order, and the value stating no Ordinal at the END rather than
    // at index 0. Under the old rule "Custom" takes 40 — colliding with Fanfold; under the
    // current one it is 0.
    private const int FanfoldOrdinal = 40;

    private static string NewTempDir()
    {
        var dir = TestScratch.FlatDir("bc-symbol-cache-enum-ordinal-version-tests-");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteApp(string dir, string fileName, string enumName)
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
                  "EnumTypes": [
                    {
                      "Id": 90310,
                      "Name": "{{enumName}}",
                      "Values": [
                        { "Name": "A3", "Ordinal": 8 },
                        { "Name": "GermanStandardFanfold", "Ordinal": 40 },
                        { "Name": "Custom" }
                      ]
                    }
                  ]
                }
                """);
        }
        return appPath;
    }

    [Fact]
    public void Get_StalePreviousVersionEntryWithTheOldOrdinalRule_IsIgnored_AndTheAppIsReparsed()
    {
        var dir = NewTempDir();
        try
        {
            var enumName = "CVT Paper Kind " + Guid.NewGuid().ToString("N");
            var appPath = WriteApp(dir, "enum-" + Guid.NewGuid().ToString("N") + ".app", enumName);

            BcAppSymbolCache.ResetProcessCacheForTests();
            var contentHash = BcAppSymbolCache.ComputeAppContentHash(appPath);

            // The exact path a CacheVersion=40 build would have written to for this same
            // .app content — a real machine's cache, unchanged .app bytes, pre-#3805 build.
            //
            // 40 is HARDCODED, deliberately, and must not be rewritten as
            // `CacheVersionForTests - 1`. That expression moves with the constant, so it
            // keeps planting the payload one version below whatever the constant says and
            // the test passes even when the bump is reverted — measured: mutating 41 back
            // to 40 left both tests GREEN. A literal is what makes the mutation bite, and
            // it is why the sibling v13 test hardcodes 13.
            const int VersionCarryingTheOldOrdinalRule = 40;
            var staleCachePath = BcAppSymbolCache.CachePathForVersionForTests(
                appPath, contentHash, VersionCarryingTheOldOrdinalRule);
            Directory.CreateDirectory(Path.GetDirectoryName(staleCachePath)!);

            // A payload carrying the OLD rule's answer: Custom got 40 by "previous + 1",
            // colliding with GermanStandardFanfold. Shape-identical to a current payload,
            // which is exactly why PayloadShape cannot discriminate it.
            File.WriteAllText(staleCachePath, $$"""
                {
                  "ContentHash": "{{contentHash}}",
                  "Tables": [],
                  "Enums": [
                    {
                      "Id": 90310, "Name": "{{enumName}}",
                      "Options": [ "A3", "GermanStandardFanfold", "Custom" ],
                      "Indexes": [ 8, 40, 40 ],
                      "Implementations": [ [], [], [] ],
                      "Captions": [ null, null, null ],
                      "DefaultImplementations": null, "UnknownImplementations": null
                    }
                  ],
                  "Queries": [], "Objects": null, "Reports": null, "Pages": null
                }
                """);

            Assert.Equal(0, BcAppSymbolCache.ParseInvocationCountForTests(appPath));

            var symbols = BcAppSymbolCache.Get(appPath);

            var parsed = Assert.Single(symbols.Enums, e => e.Name == enumName);

            // The decisive assertions. Custom is 0 — only possible if the stale entry was
            // NOT served — and the .app was genuinely reparsed rather than the stale file
            // being read as a HIT.
            Assert.Equal(new[] { "A3", "GermanStandardFanfold", "Custom" }, parsed.Options);
            Assert.Equal(new[] { 8, FanfoldOrdinal, 0 }, parsed.Indexes);
            Assert.Equal(1, BcAppSymbolCache.ParseInvocationCountForTests(appPath));

            // The consequence, asserted directly: no two values share an ordinal. The stale
            // payload violates this, so serving it could not satisfy this line.
            Assert.Equal(parsed.Indexes.Count, parsed.Indexes.Distinct().Count());

            // Prove the test's own premise rather than only the outcome: a version bump
            // MOVES the on-disk location, it does not patch the old file.
            var currentCachePath = BcAppSymbolCache.CachePathForVersionForTests(
                appPath, contentHash, BcAppSymbolCache.CacheVersionForTests);
            Assert.NotEqual(staleCachePath, currentCachePath);
            Assert.True(File.Exists(currentCachePath),
                $"Expected a fresh cache entry at the current-version path {currentCachePath}");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// Negative companion: a FRESH entry already carrying the correct ordinals must still be
    /// served as a genuine HIT, so the bump does not turn every enum lookup into an
    /// unconditional reparse. Two Get() calls across a simulated separate process
    /// (ProcessCache cleared) must reparse only once.
    /// </summary>
    [Fact]
    public void Get_FreshEntryWithTheCorrectOrdinals_IsAGenuineHitOnTheSecondCall()
    {
        var dir = NewTempDir();
        try
        {
            var enumName = "CVT Paper Kind Hit " + Guid.NewGuid().ToString("N");
            var appPath = WriteApp(dir, "enum-hit-" + Guid.NewGuid().ToString("N") + ".app", enumName);

            BcAppSymbolCache.ResetProcessCacheForTests();
            var first = BcAppSymbolCache.Get(appPath);
            Assert.Equal(new[] { 8, FanfoldOrdinal, 0 }, Assert.Single(first.Enums, e => e.Name == enumName).Indexes);
            Assert.Equal(1, BcAppSymbolCache.ParseInvocationCountForTests(appPath));

            // A separate process would have an empty ProcessCache but the same on-disk entry.
            BcAppSymbolCache.ResetProcessCacheForTests();
            var second = BcAppSymbolCache.Get(appPath);

            Assert.Equal(new[] { 8, FanfoldOrdinal, 0 }, Assert.Single(second.Enums, e => e.Name == enumName).Indexes);
            Assert.Equal(1, BcAppSymbolCache.ParseInvocationCountForTests(appPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
