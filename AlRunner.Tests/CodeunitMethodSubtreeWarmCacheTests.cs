// CodeunitMethodSubtreeWarmCacheTests — the AttributedMethods member #3788 added to ObjectSymbol
// is re-derived rather than replayed from a cache entry written before it existed.
//
// WHY A SEPARATE FILE FROM CodeunitMethodSubtreeDerivationTests
//   The same split CodeunitSymbolNamespaceWarmCacheTests makes, for the same reason. Its sibling
//   asserts what the projection ANSWERS, where a concurrent CacheRoots override can only turn a
//   HIT into a MISS — the key is content-addressed, so a re-parse of the same bytes reaches the
//   same result. This class asserts the MISS ITSELF, through ParseInvocationCountForTests and
//   through which on-disk path exists afterwards, and that is exactly what another thread's
//   override destroys. So it joins CacheRootsSerialCollection and gets the static to itself.
//
// WHY A WARM RUN IS NOT OPTIONAL HERE (local-test-scope.md)
//   BcAppSymbolCache sits between this parse and its observable, and three defects in one
//   session were each correct cold and wrong warm — #3882, #3908 and #3913, none of whose
//   authors thought they were touching a cache. CI cannot catch that class at all: it
//   provisions fresh every leg, so the legs stay green forever while every repeat local run is
//   wrong.
//
// WHAT IT PINS, AND WHY NO CacheVersion BUMP IS IN THIS CHANGE
//   The key is path|hash:<content>|v<CacheVersion>|shape:<PayloadShape>. A PARSE-only change —
//   same record shape, different answer — is invisible to the fingerprint and needs the integer
//   bumped (v35, v36, v38..v41 each did). A RECORD-SHAPE change re-keys on its own.
//
//   Which of the two this is was MEASURED, not reasoned, through the PayloadShapeForTests seam
//   off both builds: ObjectSymbol gained AttributedMethods and is reachable from CachePayload,
//   so PayloadShape moved from bfd28490a3c6a512 on origin/main to 4b1082f7e863dbec here, with
//   CacheVersion unchanged at 42. No bump is therefore correct, and this test is what keeps that
//   a fact rather than a claim: it plants a payload at the path a PRE-FIX build wrote to and
//   asserts the current build does not serve it. The fingerprint being in the key is otherwise
//   invisible, because the key is hashed into a filename.

using System.IO.Compression;
using System.Text;
using System.Xml;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Reads the on-disk cache directly and asserts a MISS, so it needs CacheRoots to itself — see
// this file's header and CacheRootsSerialCollection.
[Collection(CacheRootsSerialCollection.Name)]
public sealed class CodeunitMethodSubtreeWarmCacheTests : IDisposable
{
    private const string MetaNs = "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects";
    private const int PublishersOnly = 61060;

    private readonly string _root;

    public CodeunitMethodSubtreeWarmCacheTests()
    {
        _root = TestScratch.Dir("al-runner-codeunit-method-subtree-warm-cache");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        RecordPatches.ResetForReload();
        RecordPatches.ClearCodeunitSubscriberWitnessForTests();
        BcAppSymbolCache.ResetProcessCacheForTests();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>Two publishers and an unattributed procedure between them, so a payload that
    /// replayed a stale parse would be visible as a different SEQUENCE and not only as an
    /// absence.</summary>
    private static readonly string SymbolReference = $$"""
        {
          "RuntimeVersion": "15.1",
          "AppId": "3d9c1a77-2b4e-4f10-8c62-5e0d9a4b7c33",
          "Name": "Method Subtree Warm Cache Fixture",
          "Namespaces": [
            {
              "Name": "Fixture",
              "Codeunits": [
                {
                  "Id": {{PublishersOnly}},
                  "Name": "Publishers Only",
                  "Properties": [],
                  "Methods": [
                    { "Id": 111, "Name": "OnBeforeDoWork",
                      "Attributes": [ { "Name": "IntegrationEvent" } ] },
                    { "Id": 100, "Name": "PlainProcedure", "Attributes": [] },
                    { "Id": 222, "Name": "OnAfterDoWork",
                      "Attributes": [ { "Name": "InternalEvent" } ] }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private static List<(int Id, string Name)> Methods(int codeunitId)
    {
        var xml = RecordPatches.TryBuildCodeunitMetadataEquivalenceXml(codeunitId);
        Assert.True(xml is not null, $"the runner derived no metadata for codeunit {codeunitId}");
        var doc = new XmlDocument();
        doc.LoadXml(xml!);
        var methods = doc.DocumentElement!
            .GetElementsByTagName("Methods", MetaNs).OfType<XmlElement>().FirstOrDefault();
        if (methods is null) return new List<(int, string)>();
        return methods.ChildNodes.OfType<XmlElement>()
            .Where(e => e.LocalName == "Method")
            .Select(e => (int.Parse(e.GetAttribute("ID")), e.GetAttribute("Name")))
            .ToList();
    }

    /// <summary>
    /// A payload written before <c>ObjectSymbol</c> carried <c>AttributedMethods</c> is not
    /// served warm: the shape fingerprint is part of the key, so the current build reads from a
    /// different path and re-parses.
    /// </summary>
    [Fact]
    public void A_payload_written_before_the_symbol_carried_methods_is_not_served_warm()
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

        // The shape a build WITHOUT AttributedMethods produced. Hardcoded rather than computed
        // from the live fingerprint, for the reason the sibling hardcodes its own: an expression
        // that moves with the constant keeps planting the payload wherever the constant points,
        // and the test then passes even when the change is reverted.
        const string ShapeBeforeMethodsWereCarried = "bfd28490a3c6a512";
        Assert.NotEqual(ShapeBeforeMethodsWereCarried, BcAppSymbolCache.PayloadShapeForTests);

        var stalePath = BcAppSymbolCache.CachePathForShapeForTests(
            appPath, contentHash, BcAppSymbolCache.CacheVersionForTests, ShapeBeforeMethodsWereCarried);
        Directory.CreateDirectory(Path.GetDirectoryName(stalePath)!);

        // A pre-fix payload: the codeunit is present and carries no methods member at all —
        // exactly what a machine that ran the old build has on disk right now.
        File.WriteAllText(stalePath, $$"""
            {
              "ContentHash": "{{contentHash}}",
              "Tables": [], "Enums": [], "Queries": [],
              "Objects": [
                { "Kind": "Codeunit", "Id": {{PublishersOnly}}, "Name": "Publishers Only",
                  "Caption": null, "TableNo": null, "SingleInstance": false,
                  "Subtype": null, "TargetObjectName": null, "ALNamespace": "Fixture",
                  "InherentEntitlements": null, "InherentPermissions": null }
              ],
              "Reports": null, "Pages": null
            }
            """);

        Assert.Equal(0, BcAppSymbolCache.ParseInvocationCountForTests(appPath));

        RecordPatches.ResetForReload();
        RecordPatches.ClearCodeunitSubscriberWitnessForTests();
        RecordPatches.AddBcAppPath(appPath);
        RecordPatches.RegisterCodeunitSubscriberWitness(
            appPath, Array.Empty<int>(), new[] { PublishersOnly });

        // The decisive assertion: the subtree is derived, which the stale payload cannot supply
        // — it does not carry the member at all. Serving it would answer an empty list.
        Assert.Equal(
            new[] { (111, "OnBeforeDoWork"), (222, "OnAfterDoWork") },
            Methods(PublishersOnly));
        Assert.Equal(1, BcAppSymbolCache.ParseInvocationCountForTests(appPath));

        // The test's own premise, asserted rather than assumed: a shape change MOVES the on-disk
        // location, it does not patch the old file.
        var currentPath = BcAppSymbolCache.CachePathForShapeForTests(
            appPath, contentHash, BcAppSymbolCache.CacheVersionForTests, BcAppSymbolCache.PayloadShapeForTests);
        Assert.NotEqual(stalePath, currentPath);
        Assert.True(File.Exists(currentPath),
            $"expected a fresh cache entry at the current-shape path {currentPath}");

        // And the SECOND read — the actual warm run — answers the same thing, in the same order,
        // from that fresh entry rather than by parsing again. This is the half local-test-scope.md
        // exists for: #3908's fix replayed the buggy value from a cache whose key had no term for
        // the parse, and only a second read against one cache root showed it.
        BcAppSymbolCache.ResetProcessCacheForTests();
        RecordPatches.ResetForReload();
        RecordPatches.ClearCodeunitSubscriberWitnessForTests();
        RecordPatches.AddBcAppPath(appPath);
        RecordPatches.RegisterCodeunitSubscriberWitness(
            appPath, Array.Empty<int>(), new[] { PublishersOnly });

        Assert.Equal(
            new[] { (111, "OnBeforeDoWork"), (222, "OnAfterDoWork") },
            Methods(PublishersOnly));
        Assert.Equal(1, BcAppSymbolCache.ParseInvocationCountForTests(appPath));
    }
}
