// CodeunitSymbolNamespaceWarmCacheTests — the three members #3788 added to ObjectSymbol are
// re-derived rather than replayed from a cache entry written before they existed.
//
// WHY THIS IS A SEPARATE FILE FROM CodeunitSymbolNamespaceAndInherentMaskTests
//   A test class belongs to exactly one xunit collection, and this one needs a different one
//   from its sibling. The sibling asserts what the projection ANSWERS, and a concurrent
//   CacheRoots override can only turn a HIT into a MISS there — the key is content-addressed, so
//   a re-parse of the same bytes reaches the same result (the argument
//   BcAppRegistrationEpochInvalidationTests makes for staying in RecordPatchesSerialCollection).
//
//   This test asserts the MISS ITSELF, through ParseInvocationCountForTests and through which
//   on-disk path exists afterwards. That distinction is exactly what another thread's override
//   destroys, so this class joins CacheRootsSerialCollection and gets the process-static
//   override to itself.
//
// WHAT IT PINS, AND WHY A CacheVersion BUMP IS NOT IN THIS CHANGE
//   BcAppSymbolCache is keyed on path|hash:<content>|v<CacheVersion>|shape:<PayloadShape>. A
//   PARSE-only change (same record shape, different answer) is invisible to the fingerprint and
//   needs the integer bumped — that is v35, v36, v38, v39, v40 and v41. A RECORD-SHAPE change
//   re-keys on its own.
//
//   Which of the two this is was MEASURED, not reasoned: ObjectSymbol gained three members and
//   is reachable from CachePayload, so PayloadShape moved from ccb081fbed1589bf on origin/main
//   to b82cf78cd0b9123a here, read off both builds through the PayloadShapeForTests seam. No
//   bump is therefore correct — and this test is what keeps that a fact rather than a claim.

using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Reads the on-disk cache directly and asserts a MISS, so it needs CacheRoots to itself — see
// this file's header and CacheRootsSerialCollection.
[Collection(CacheRootsSerialCollection.Name)]
public sealed class CodeunitSymbolNamespaceWarmCacheTests : IDisposable
{
    private const int NestedTwoDeep = 61051;
    private const int DirectExecute = 61054;
    private const int IndirectExecute = 61055;

    private readonly string _root;

    public CodeunitSymbolNamespaceWarmCacheTests()
    {
        _root = TestScratch.Dir("al-runner-codeunit-namespace-warm-cache");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        RecordPatches.ResetForReload();
        BcAppSymbolCache.ResetProcessCacheForTests();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // The same fixture shape as the sibling, reduced to the three codeunits this test reads.
    private static readonly string SymbolReference = $$"""
        {
          "RuntimeVersion": "15.1",
          "AppId": "0c3a8be2-51d7-4f16-93f2-0d7ac4b1e5a8",
          "Name": "Namespace Warm Cache Fixture",
          "Namespaces": [
            {
              "Name": "System",
              "Namespaces": [
                {
                  "Name": "Utilities",
                  "Codeunits": [
                    { "Id": {{NestedTwoDeep}}, "Name": "Two Deep", "Properties": [] },
                    {
                      "Id": {{DirectExecute}},
                      "Name": "Direct Execute",
                      "Properties": [
                        { "Name": "InherentEntitlements", "Value": "X" },
                        { "Name": "InherentPermissions", "Value": "X" }
                      ]
                    },
                    {
                      "Id": {{IndirectExecute}},
                      "Name": "Indirect Execute",
                      "Properties": [
                        { "Name": "InherentEntitlements", "Value": "x" }
                      ]
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private static System.Xml.XmlElement Projection(int codeunitId)
    {
        var xml = RecordPatches.TryBuildCodeunitMetadataEquivalenceXml(codeunitId);
        Assert.True(xml is not null, $"the runner derived no metadata for codeunit {codeunitId}");
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(xml!);
        return doc.DocumentElement!;
    }

    /// <summary>
    /// The warm-cache half (local-test-scope.md): there IS a cache between this parse and its
    /// observable — <c>BcAppSymbolCache</c>, keyed on
    /// <c>path|hash:&lt;content&gt;|v&lt;CacheVersion&gt;|shape:&lt;PayloadShape&gt;</c> — so a
    /// fix correct cold can be wrong warm, which is what #3908 and #3913 were.
    ///
    /// <para><b>Which discriminator applies was MEASURED, not reasoned.</b> Adding three members
    /// to <c>ObjectSymbol</c> is a record SHAPE change, and <c>ObjectSymbol</c> is reachable from
    /// <c>CachePayload</c>, so <c>PayloadShape</c> re-keys on its own:
    /// <c>ccb081fbed1589bf</c> on origin/main became <c>b82cf78cd0b9123a</c> here, read through
    /// the <c>PayloadShapeForTests</c> seam off both builds. That is why NO <c>CacheVersion</c>
    /// bump is in this change — the parse-only trap v35/v36/v38..v41 each paid for does not
    /// apply when the shape itself moved.</para>
    ///
    /// <para>This test is what keeps that a fact rather than a claim. It plants a payload at the
    /// path a PRE-FIX build wrote to and asserts the current build does not serve it — the
    /// fingerprint being in the key is otherwise invisible, because the key is hashed into a
    /// filename.</para>
    /// </summary>
    [Fact]
    public void A_payload_written_before_the_symbol_gained_these_members_is_not_served_warm()
    {
        var appPath = Path.Combine(_root, "warm-cache.app");
        using (var zip = new FileStream(appPath, FileMode.Create))
        using (var za = new ZipArchive(zip, ZipArchiveMode.Create))
        {
            var entry = za.CreateEntry("SymbolReference.json");
            using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
            w.Write(SymbolReference);
        }

        BcAppSymbolCache.ResetProcessCacheForTests();
        var contentHash = BcAppSymbolCache.ComputeAppContentHash(appPath);

        // The shape a build WITHOUT the three members produced. Hardcoded rather than computed
        // from the live fingerprint, for the reason the v41 sibling hardcodes its version: an
        // expression that moves with the constant keeps planting the payload wherever the
        // constant points, and the test then passes even when the change is reverted.
        const string ShapeBeforeTheMembersWereAdded = "ccb081fbed1589bf";
        Assert.NotEqual(ShapeBeforeTheMembersWereAdded, BcAppSymbolCache.PayloadShapeForTests);

        var stalePath = BcAppSymbolCache.CachePathForShapeForTests(
            appPath, contentHash, BcAppSymbolCache.CacheVersionForTests, ShapeBeforeTheMembersWereAdded);
        Directory.CreateDirectory(Path.GetDirectoryName(stalePath)!);

        // A pre-fix payload: the codeunit is present, and every one of the three members is
        // absent — exactly what a machine that ran the old build has on disk right now.
        File.WriteAllText(stalePath, $$"""
            {
              "ContentHash": "{{contentHash}}",
              "Tables": [], "Enums": [], "Queries": [],
              "Objects": [
                { "Kind": "Codeunit", "Id": {{NestedTwoDeep}}, "Name": "Two Deep",
                  "Caption": null, "TableNo": null, "SingleInstance": false,
                  "Subtype": null, "TargetObjectName": null }
              ],
              "Reports": null, "Pages": null
            }
            """);

        Assert.Equal(0, BcAppSymbolCache.ParseInvocationCountForTests(appPath));

        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(appPath);

        // The decisive assertion: the namespace is derived, which the stale payload cannot
        // supply — it does not carry the member at all. Serving it would answer "".
        Assert.Equal("System.Utilities", Projection(NestedTwoDeep).GetAttribute("ALNamespace"));
        Assert.Equal(1, BcAppSymbolCache.ParseInvocationCountForTests(appPath));

        // The test's own premise, asserted rather than assumed: a shape change MOVES the
        // on-disk location, it does not patch the old file.
        var currentPath = BcAppSymbolCache.CachePathForShapeForTests(
            appPath, contentHash, BcAppSymbolCache.CacheVersionForTests, BcAppSymbolCache.PayloadShapeForTests);
        Assert.NotEqual(stalePath, currentPath);
        Assert.True(File.Exists(currentPath),
            $"expected a fresh cache entry at the current-shape path {currentPath}");

        // And the SECOND read — the actual warm run — answers the same thing, from that fresh
        // entry rather than by parsing again.
        BcAppSymbolCache.ResetProcessCacheForTests();
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(appPath);
        Assert.Equal("System.Utilities", Projection(NestedTwoDeep).GetAttribute("ALNamespace"));
        Assert.Equal("16", Projection(DirectExecute).GetAttribute("InherentEntitlements"));
        Assert.Equal("512", Projection(IndirectExecute).GetAttribute("InherentEntitlements"));
    }
}
