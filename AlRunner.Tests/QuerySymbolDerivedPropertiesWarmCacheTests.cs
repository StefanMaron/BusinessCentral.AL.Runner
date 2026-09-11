// QuerySymbolDerivedPropertiesWarmCacheTests — the two members #3798 added to QuerySymbol are
// re-derived rather than replayed from a cache entry written before they existed.
//
// WHY A SEPARATE FILE FROM QuerySymbolDerivedMetaQueryPropertiesTests
//   A test class belongs to exactly one xunit collection, and this one needs a different one
//   from its sibling. The sibling asserts what the design object ANSWERS, where a concurrent
//   CacheRoots override can only turn a HIT into a MISS — the key is content-addressed, so a
//   re-parse of the same bytes reaches the same result.
//
//   This test asserts the MISS ITSELF, through ParseInvocationCountForTests and through which
//   on-disk path exists afterwards, which is exactly what another thread's override destroys.
//   So it joins CacheRootsSerialCollection and gets the process-static override to itself.
//   Same split, same reason, as CodeunitSymbolNamespaceWarmCacheTests.
//
// WHY IT EXISTS AT ALL, AND WHY NO CacheVersion BUMP IS IN THIS CHANGE
//   BcAppSymbolCache is keyed on path|hash:<content>|v<CacheVersion>|shape:<PayloadShape>, so
//   there IS a cache between this parse and its observable and a fix correct cold can be wrong
//   warm — what #3908 and #3913 were (local-test-scope.md).
//
//   A PARSE-only change (same record shape, different answer) is invisible to the fingerprint
//   and needs the integer bumped. A RECORD-SHAPE change re-keys on its own. Which of the two
//   this is was MEASURED, not reasoned: QuerySymbol gained two members and is reachable from
//   CachePayload, so PayloadShape moved from b82cf78cd0b9123a on origin/main to the current
//   value, read through the PayloadShapeForTests seam off both builds. No bump is therefore
//   correct — and this test is what keeps that a fact rather than a claim, because the
//   fingerprint being in the key is otherwise invisible: the key is hashed into a filename.

using System.IO.Compression;
using System.Reflection;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Reads the on-disk cache directly and asserts a MISS, so it needs CacheRoots to itself — see
// this file's header and CacheRootsSerialCollection.
[Collection(CacheRootsSerialCollection.Name)]
public sealed class QuerySymbolDerivedPropertiesWarmCacheTests : IDisposable
{
    private const int DirectExecute = 61078;
    private const int LinkedTable = 61079;

    private readonly string _root;

    public QuerySymbolDerivedPropertiesWarmCacheTests()
    {
        _root = TestScratch.Dir("al-runner-query-derived-warm-cache");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        RecordPatches.ResetForReload();
        BcAppSymbolCache.ResetProcessCacheForTests();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static readonly string SymbolReference = $$"""
        {
          "RuntimeVersion": "15.1",
          "AppId": "9b2e7c15-6d84-4a30-8f19-3c5d7e2a604b",
          "Name": "Query Warm Cache Fixture",
          "Tables": [
            {
              "Id": {{LinkedTable}},
              "Name": "Warm Query Source",
              "Fields": [
                { "Id": 1, "Name": "Code", "TypeDefinition": { "Name": "Code", "Length": 20 } }
              ]
            }
          ],
          "Namespaces": [
            {
              "Name": "System",
              "Queries": [
                {
                  "Id": {{DirectExecute}},
                  "Name": "Warm Direct Execute",
                  "Properties": [
                    { "Name": "InherentEntitlements", "Value": "X" },
                    { "Name": "InherentPermissions", "Value": "X" }
                  ],
                  "Elements": [
                    {
                      "Id": 1, "Name": "Root", "RelatedTable": "Warm Query Source",
                      "Columns": [ { "Id": 11, "Name": "Code_Col", "SourceColumn": "Code" } ]
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private static string? MaskOf(int queryId, string property)
    {
        var design = RecordPatches.TryBuildQueryMetadataEquivalenceDesign(queryId);
        Assert.True(design is not null, $"the runner built no MetaQuery design for query {queryId}");
        return design!.GetType()
            .GetProperty(property, BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(design)?.ToString();
    }

    /// <summary>
    /// A payload written by a build that predates the two members is not served warm: the shape
    /// change moves the on-disk location, so the current build misses, re-parses, and derives
    /// the masks the stale payload could not have supplied.
    /// </summary>
    [Fact]
    public void A_payload_written_before_QuerySymbol_gained_the_masks_is_not_served_warm()
    {
        var appPath = Path.Combine(_root, "warm-query.app");
        using (var zip = new FileStream(appPath, FileMode.Create))
        using (var za = new ZipArchive(zip, ZipArchiveMode.Create))
        {
            var entry = za.CreateEntry("SymbolReference.json");
            using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
            w.Write(SymbolReference);
        }

        BcAppSymbolCache.ResetProcessCacheForTests();
        var contentHash = BcAppSymbolCache.ComputeAppContentHash(appPath);

        // The shape a build WITHOUT the two members produced — origin/main at 487c5925.
        // Hardcoded rather than read from the live fingerprint: an expression that moves with
        // the constant keeps planting the payload wherever the constant points, and the test
        // then passes even when the change is reverted.
        const string ShapeBeforeTheMasksWereAdded = "b82cf78cd0b9123a";
        Assert.NotEqual(ShapeBeforeTheMasksWereAdded, BcAppSymbolCache.PayloadShapeForTests);

        var stalePath = BcAppSymbolCache.CachePathForShapeForTests(
            appPath, contentHash, BcAppSymbolCache.CacheVersionForTests, ShapeBeforeTheMasksWereAdded);
        Directory.CreateDirectory(Path.GetDirectoryName(stalePath)!);

        // A pre-fix payload: the query is present with its structure intact, and BOTH masks are
        // absent from the record — exactly what a machine that ran the old build holds now.
        File.WriteAllText(stalePath, $$"""
            {
              "ContentHash": "{{contentHash}}",
              "Tables": [], "Enums": [],
              "Queries": [
                { "Id": {{DirectExecute}}, "Name": "Warm Direct Execute", "QueryType": null,
                  "Caption": null, "OrderBy": null, "TopNumberOfRowsToReturn": 0,
                  "DataItems": [] }
              ],
              "Objects": [], "Reports": null, "Pages": null
            }
            """);

        Assert.Equal(0, BcAppSymbolCache.ParseInvocationCountForTests(appPath));

        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(appPath);

        // The decisive assertion: the mask is derived, which the stale payload cannot supply —
        // it does not carry the member at all, so serving it would answer None.
        Assert.Equal("Execute", MaskOf(DirectExecute, "InherentEntitlements"));
        Assert.Equal(1, BcAppSymbolCache.ParseInvocationCountForTests(appPath));

        // The test's own premise, asserted rather than assumed: a shape change MOVES the
        // on-disk location, it does not patch the old file.
        var currentPath = BcAppSymbolCache.CachePathForShapeForTests(
            appPath, contentHash, BcAppSymbolCache.CacheVersionForTests, BcAppSymbolCache.PayloadShapeForTests);
        Assert.NotEqual(stalePath, currentPath);
        Assert.True(File.Exists(currentPath),
            $"expected a fresh cache entry at the current-shape path {currentPath}");

        // And the SECOND read — the actual warm run — answers the same thing, from that fresh
        // entry rather than by parsing again. Caption is asserted here too: it is derived in the
        // BUILDER rather than the parse, so a HIT that restored the record correctly could still
        // answer wrongly if the derivation sat on the parse side.
        BcAppSymbolCache.ResetProcessCacheForTests();
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(appPath);
        Assert.Equal("Execute", MaskOf(DirectExecute, "InherentEntitlements"));
        Assert.Equal("Execute", MaskOf(DirectExecute, "InherentPermissions"));
        Assert.Equal("Warm Direct Execute", MaskOf(DirectExecute, "Caption"));
    }
}
