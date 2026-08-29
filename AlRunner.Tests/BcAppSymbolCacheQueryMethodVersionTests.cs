// BcAppSymbolCacheQueryMethodVersionTests — proves the CacheVersion bump for
// QueryColumnSymbol.Method (issue #2137) actually invalidates a stale on-disk entry,
// instead of silently reintroducing the bug the field was added to fix.
//
// Gap being fixed
// ----------------
// QueryColumnSymbol.Method was added so BcAppSymbolCache.ParseQueryColumns carries the AL
// `Method = Sum/Count/Average/Min/Max` property through to RecordPatches.QueryProjection's
// GROUP BY aggregation. CachePayload (which holds QuerySymbol -> QueryDataItemSymbol ->
// QueryColumnSymbol) is persisted to disk and read back via JsonSerializer, keyed by
// `{fullPath}|hash:{contentHash}|v{CacheVersion}` (BcAppSymbolCache.Get). A machine whose
// on-disk cache was written by the PREVIOUS build has an entry whose JSON has no "Method"
// property at all — the .app's content hash has not changed, so without a CacheVersion
// bump that entry keeps matching the key and Method deserialises as null on every
// subsequent read, silently reintroducing #2137 (AggregationType stays None, the query
// returns raw ungrouped rows) even though the fix is otherwise fully deployed. Bumping
// CacheVersion changes the KEY STRING itself (see BcAppSymbolCache.CachePath — a SHA-256
// hash of the key), so a stale v13 entry sits at a different on-disk filename entirely and
// is never looked up; the .app is reparsed, and the fresh parse carries Method correctly.
//
// This is the same shape as every prior CacheVersion bump documented above the constant
// (v9 LookupPageName, v11 SourceTableTemporary, v12 EnumSymbol.Captions, v13 PageSymbol's
// PageType/Controls/CardPageName) — the fix for a "field defaults to a value that makes
// old data readable but WRONG" bug is not just adding the field, it's making sure an old
// payload can never satisfy the new key at all.
//
// Test strategy
// -------------
// A test that only writes and reads back a FRESH (v14) cache entry cannot catch a missing
// version bump — a fresh entry always has Method, whichever version number the key embeds.
// The decisive test therefore constructs the on-disk payload a v13 BUILD would have
// written (a real .app's content hash, but a QueryColumnSymbol JSON shape with no "Method"
// property at all — Query cache entries never carried it before this issue), places it at
// the exact v13-keyed path, then calls Get() with the CURRENT code and asserts BOTH that
// Method survives AND that BcAppSymbolCache actually reparsed (ParseInvocationCountForTests
// == 1) rather than serving a HIT that merely happened to already have the right shape.
//
// Locating "the exact v13-keyed path" reuses BcAppSymbolCache.CachePathForVersionForTests
// — an internal test seam that delegates to the SAME private CachePath hashing formula
// Get() itself uses — rather than a hand-rolled copy of that formula in this file. A
// review of an earlier version of this test caught exactly the risk a copy would carry:
// if CachePath's hashing/layout ever changed, a duplicated copy here would keep computing
// A path, just not the one Get() actually consults, and this test would then pass for the
// wrong reason (write to a location nothing reads, MISS-and-reparse "succeeds" whether or
// not the version bump is doing anything at all). Delegating to the real formula makes
// that drift impossible instead of merely documented.
using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// #1821: BcAppSymbolCache.Get() resolves its on-disk path through the process-global
// CacheRoots override, so this joins CacheRootsSerialCollection to avoid racing
// CacheRootsTests's SetOverride calls — see that collection's header for why. This class
// does not call SetOverride itself; like the other BcAppSymbolCache test classes it relies
// on the content-addressed key (a fresh Guid-unique .app path per test) to make collisions
// with the real shared bc-symbols cache directory practically impossible.
[Collection(CacheRootsSerialCollection.Name)]
public sealed class BcAppSymbolCacheQueryMethodVersionTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bc-symbol-cache-query-method-version-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteApp(string dir, string fileName, string queryName)
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
                  "Queries": [
                    {
                      "Id": 90210,
                      "Name": "{{queryName}}",
                      "Properties": [ { "Name": "QueryType", "Value": "Normal" } ],
                      "Elements": [
                        {
                          "Id": 1,
                          "Name": "Order",
                          "RelatedTable": "CVT Order",
                          "Properties": [],
                          "Columns": [
                            {
                              "Id": 1,
                              "Name": "TotalAmount",
                              "SourceColumn": "Amount",
                              "Properties": [ { "Name": "Method", "Value": "Sum" } ]
                            }
                          ],
                          "Filters": []
                        }
                      ]
                    }
                  ]
                }
                """);
        }
        return appPath;
    }

    [Fact]
    public void Get_StaleV13EntryWithNoMethodProperty_IsIgnored_AndTheAppIsReparsedWithMethodIntact()
    {
        var dir = NewTempDir();
        try
        {
            var queryName = "CVT Order Sum " + Guid.NewGuid().ToString("N");
            var appPath = WriteApp(dir, "agg-" + Guid.NewGuid().ToString("N") + ".app", queryName);

            BcAppSymbolCache.ResetProcessCacheForTests();
            var contentHash = BcAppSymbolCache.ComputeAppContentHash(appPath);

            // The exact path a CacheVersion=13 build would have written to for this SAME
            // .app content — a real machine's cache, unchanged .app bytes, pre-#2137
            // runner build. Computed via the real CachePath formula (through the exposed
            // test seam), never a copy of it — see the file header.
            var staleCachePath = BcAppSymbolCache.CachePathForVersionForTests(appPath, contentHash, cacheVersion: 13);
            Directory.CreateDirectory(Path.GetDirectoryName(staleCachePath)!);
            // A v13 QueryColumnSymbol payload: no "Method" property in the JSON at all
            // (the field did not exist yet) — exactly what a pre-fix TryWrite produced.
            File.WriteAllText(staleCachePath, $$"""
                {
                  "ContentHash": "{{contentHash}}",
                  "Tables": [], "Enums": [],
                  "Queries": [
                    {
                      "Id": 90210, "Name": "{{queryName}}", "QueryType": "Normal", "Caption": null, "OrderBy": null,
                      "TopNumberOfRowsToReturn": 0,
                      "DataItems": [
                        {
                          "Id": 1, "Name": "Order", "RelatedTable": "CVT Order", "SqlJoinType": null, "DataItemLink": null,
                          "Columns": [ { "Id": 1, "Name": "TotalAmount", "SourceColumn": "Amount", "Caption": null } ],
                          "Filters": [], "DataItems": []
                        }
                      ]
                    }
                  ],
                  "Objects": null, "Reports": null, "Pages": null
                }
                """);

            Assert.Equal(0, BcAppSymbolCache.ParseInvocationCountForTests(appPath));

            var symbols = BcAppSymbolCache.Get(appPath);

            var query = Assert.Single(symbols.Queries, q => q.Name == queryName);
            var dataItem = Assert.Single(query.DataItems);
            var column = Assert.Single(dataItem.Columns);

            // The decisive assertions: Method survived (only possible if the stale
            // Method-less v13 entry was NOT served), and the .app was genuinely reparsed
            // (ParseInvocationCount == 1) rather than the stale file being read as a HIT
            // that merely happened, by construction of this test, to already be correct.
            Assert.Equal("Sum", column.Method);
            Assert.Equal(1, BcAppSymbolCache.ParseInvocationCountForTests(appPath));

            // Prove the test's own premise, not just the outcome: the freshly-reparsed
            // entry now lives at the CURRENT version's path (distinct from the stale v13
            // one above), which is the concrete mechanism the whole test is claiming — a
            // version bump moves the on-disk location, it doesn't patch the old file.
            var currentCachePath = BcAppSymbolCache.CachePathForVersionForTests(
                appPath, contentHash, BcAppSymbolCache.CacheVersionForTests);
            Assert.NotEqual(staleCachePath, currentCachePath);
            Assert.True(File.Exists(currentCachePath),
                $"Expected a fresh cache entry at the current-version path {currentCachePath}");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// Negative companion: a FRESH (current-version) cache entry that already carries
    /// Method correctly must still be served as a genuine HIT — this fix must not turn
    /// every Query cache lookup into an unconditional reparse. Two Get() calls across a
    /// simulated separate process (ProcessCache cleared, mirroring
    /// BcAppSymbolCacheContentAddressedKeyTests' own pattern) must reparse only once.
    /// </summary>
    [Fact]
    public void Get_FreshEntryWithMethodAlreadyPresent_IsAGenuineHitOnTheSecondCall()
    {
        var dir = NewTempDir();
        try
        {
            var queryName = "CVT Order Sum Hit " + Guid.NewGuid().ToString("N");
            var appPath = WriteApp(dir, "agg-hit-" + Guid.NewGuid().ToString("N") + ".app", queryName);

            BcAppSymbolCache.ResetProcessCacheForTests();
            Assert.Equal(0, BcAppSymbolCache.ParseInvocationCountForTests(appPath));

            var first = BcAppSymbolCache.Get(appPath);
            Assert.Equal("Sum", Assert.Single(Assert.Single(first.Queries, q => q.Name == queryName).DataItems).Columns.Single().Method);
            Assert.Equal(1, BcAppSymbolCache.ParseInvocationCountForTests(appPath));

            BcAppSymbolCache.ResetProcessCacheForTests();
            var second = BcAppSymbolCache.Get(appPath);

            Assert.Equal("Sum", Assert.Single(Assert.Single(second.Queries, q => q.Name == queryName).DataItems).Columns.Single().Method);
            // The on-disk (v-current) entry from the first call must be served as a HIT —
            // no second reparse.
            Assert.Equal(1, BcAppSymbolCache.ParseInvocationCountForTests(appPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
