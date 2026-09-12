// ReportSymbolNamespaceWarmCacheTests — the three members #3808 added to ReportSymbol are
// re-derived rather than replayed from a cache entry written before they existed.
//
// WHY THIS IS A SEPARATE FILE FROM ReportSymbolNamespaceAndInherentMaskTests
//   A test class belongs to exactly one xunit collection, and this one needs the CacheRoots
//   override to itself. The sibling asserts what the derivation ANSWERS, where a concurrent
//   override can only turn a HIT into a MISS and the content-addressed key makes a re-parse
//   reach the same result. This test asserts the MISS ITSELF — through
//   ParseInvocationCountForTests and through which on-disk path exists afterwards — and that
//   distinction is exactly what another thread's override destroys.
//
// WHICH DISCRIMINATOR APPLIES, AND WHY NO CacheVersion BUMP IS IN THIS CHANGE
//   BcAppSymbolCache is keyed on path|hash:<content>|v<CacheVersion>|shape:<PayloadShape>. A
//   PARSE-only change (same record shape, different answer) is invisible to the fingerprint and
//   needs the integer bumped. A RECORD-SHAPE change re-keys on its own.
//
//   This is the second, and it was MEASURED rather than reasoned: ReportSymbol gained three
//   members and is reachable from CachePayload through AppSymbols.Reports, so PayloadShape moved
//   from b82cf78cd0b9123a on origin/main to d8c3506849475c13 here — read through the
//   PayloadShapeForTests seam off BOTH builds, not computed once and assumed to have changed.
//   (b82cf78cd0b9123a is the value #3917 left behind, which is the cross-check that the seam was
//   reading main rather than a stale build.) So no bump is correct, and this test is what keeps
//   that a fact rather than a claim.

using System.IO.Compression;
using System.Text;
using System.Xml;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Reads the on-disk cache directly and asserts a MISS, so it needs CacheRoots to itself — see
// this file's header and CacheRootsSerialCollection.
[Collection(CacheRootsSerialCollection.Name)]
public sealed class ReportSymbolNamespaceWarmCacheTests : IDisposable
{
    private const int ChangePassword = 9810;

    private readonly string _root;

    public ReportSymbolNamespaceWarmCacheTests()
    {
        _root = TestScratch.Dir("al-runner-report-namespace-warm-cache");
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
          "AppId": "6b2d9f10-7c84-4e2a-9f3b-2d1c5a8e7b40",
          "Name": "Report Namespace Warm Cache Fixture",
          "Namespaces": [
            {
              "Name": "System",
              "Namespaces": [
                {
                  "Name": "Security",
                  "Namespaces": [
                    {
                      "Name": "AccessControl",
                      "Reports": [
                        {
                          "Id": {{ChangePassword}},
                          "Name": "Change Password",
                          "DataItems": [],
                          "Properties": [
                            { "Name": "ProcessingOnly", "Value": "1" },
                            { "Name": "UseRequestPage", "Value": "0" },
                            { "Name": "InherentEntitlements", "Value": "X" },
                            { "Name": "InherentPermissions", "Value": "X" }
                          ]
                        }
                      ]
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private static XmlElement Emit(BcAppSymbolCache.ReportSymbol report)
    {
        var doc = new XmlDocument();
        doc.LoadXml(RecordPatches.EmitReportXml(report, sourceExprByColumn: null));
        return doc.DocumentElement!;
    }

    private static string? Element(XmlElement root, string name)
        => root.SelectSingleNode(name) is XmlNode n ? n.InnerText : null;

    /// <summary>
    /// The warm-cache half (local-test-scope.md): there IS a cache between this parse and its
    /// observable — <c>BcAppSymbolCache</c> — so a fix correct cold can be wrong warm, which is
    /// what #3908 and #3913 were. This plants a payload at the path a PRE-FIX build wrote to and
    /// asserts the current build does not serve it; the fingerprint being part of the key is
    /// otherwise invisible, because the key is hashed into a filename.
    /// </summary>
    [Fact]
    public void A_payload_written_before_the_report_gained_these_members_is_not_served_warm()
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
        // from the live fingerprint, for the reason the codeunit sibling hardcodes its own: an
        // expression that moves with the constant keeps planting the payload wherever the
        // constant points, and the test then passes even when the change is reverted.
        const string ShapeBeforeTheMembersWereAdded = "b82cf78cd0b9123a";
        Assert.NotEqual(ShapeBeforeTheMembersWereAdded, BcAppSymbolCache.PayloadShapeForTests);

        var stalePath = BcAppSymbolCache.CachePathForShapeForTests(
            appPath, contentHash, BcAppSymbolCache.CacheVersionForTests, ShapeBeforeTheMembersWereAdded);
        Directory.CreateDirectory(Path.GetDirectoryName(stalePath)!);

        // A pre-fix payload: the report is present, with every one of the three members absent —
        // exactly what a machine that ran the old build has on disk right now.
        File.WriteAllText(stalePath, $$"""
            {
              "ContentHash": "{{contentHash}}",
              "Tables": [], "Enums": [], "Queries": [], "Objects": [],
              "Reports": [
                { "Id": {{ChangePassword}}, "Name": "Change Password", "Caption": null,
                  "ProcessingOnly": true, "UseRequestPage": false, "WordMergeDataItem": null,
                  "DataItems": [], "ReferenceSourceFileName": null }
              ],
              "Pages": []
            }
            """);

        Assert.Equal(0, BcAppSymbolCache.ParseInvocationCountForTests(appPath));

        // The decisive assertion: all three are derived, which the stale payload cannot supply —
        // it does not carry the members at all. Serving it would answer null, null, null.
        var report = Assert.Single(BcAppSymbolCache.Get(appPath).Reports);
        Assert.Equal("System.Security.AccessControl", report.ALNamespace);
        Assert.Equal("X", report.InherentEntitlements);
        Assert.Equal("X", report.InherentPermissions);
        Assert.Equal(1, BcAppSymbolCache.ParseInvocationCountForTests(appPath));

        // The test's own premise, asserted rather than assumed: a shape change MOVES the on-disk
        // location, it does not patch the old file.
        var currentPath = BcAppSymbolCache.CachePathForShapeForTests(
            appPath, contentHash, BcAppSymbolCache.CacheVersionForTests, BcAppSymbolCache.PayloadShapeForTests);
        Assert.NotEqual(stalePath, currentPath);
        Assert.True(File.Exists(currentPath),
            $"expected a fresh cache entry at the current-shape path {currentPath}");

        // And the SECOND read — the actual warm run — answers the same thing, from that fresh
        // entry rather than by parsing again. The emitted document is asserted here too, because
        // that is the observable BC's reader consumes and it is downstream of the cache.
        BcAppSymbolCache.ResetProcessCacheForTests();
        var warm = Assert.Single(BcAppSymbolCache.Get(appPath).Reports);
        Assert.Equal(1, BcAppSymbolCache.ParseInvocationCountForTests(appPath));
        Assert.Equal("System.Security.AccessControl", warm.ALNamespace);

        var xml = Emit(warm);
        Assert.Equal("System.Security.AccessControl", Element(xml, "ALNamespace"));
        Assert.Equal("16", Element(xml, "InherentEntitlements"));
        Assert.Equal("16", Element(xml, "InherentPermissions"));
    }
}
