// DependencyPagePartPropertiesWarmCacheTests — the members #4282 added to PageSymbol and
// PagePartSymbol are re-derived, not replayed from a cache entry written before they existed.
// A record-shape change re-keys BcAppSymbolCache through PayloadShape (no CacheVersion bump);
// this test is what keeps that a fact. Same pattern as QuerySymbolDerivedPropertiesWarmCacheTests.

using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(CacheRootsSerialCollection.Name)]
public sealed class DependencyPagePartPropertiesWarmCacheTests : IDisposable
{
    private const int HostWithAreaId = 88128001;
    private const int SilentPartId = 88128101;

    private readonly string _root;

    public DependencyPagePartPropertiesWarmCacheTests()
    {
        _root = TestScratch.Dir("al-runner-dep-page-part-props-warm-cache");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        RecordPatches.ResetForReload();
        BcAppSymbolCache.ResetProcessCacheForTests();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static string? SilentPartArea()
    {
        var xml = RecordPatches.TryBuildDependencyPageMetadata(HostWithAreaId);
        Assert.NotNull(xml);
        var doc = new XmlDocument();
        doc.LoadXml(xml!);
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("m", "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects");
        var part = (XmlElement)doc.DocumentElement!.SelectSingleNode(
            $"m:Content/m:Containers/m:Controls[@ID='{SilentPartId}']", ns)!;
        return part.HasAttribute("ApplicationArea") ? part.GetAttribute("ApplicationArea") : null;
    }

    private static void StripNewMembers(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var k in new[] { "ApplicationArea", "AboutTitle", "AboutText" })
                    o.Remove(k);
                foreach (var kv in o.ToList()) StripNewMembers(kv.Value);
                break;
            case JsonArray a:
                foreach (var e in a) StripNewMembers(e);
                break;
        }
    }

    [Fact]
    public void A_payload_written_before_parts_carried_their_area_is_not_served_warm()
    {
        var appPath = Path.Combine(_root, "part-props.app");
        using (var fs = new FileStream(appPath, FileMode.Create))
        using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
        using (var w = new StreamWriter(za.CreateEntry("SymbolReference.json").Open(), Encoding.UTF8))
            w.Write(DependencyPagePartPropertiesTests.SymbolReference);

        BcAppSymbolCache.ResetProcessCacheForTests();
        RecordPatches.ResetForReload();
        var contentHash = BcAppSymbolCache.ComputeAppContentHash(appPath);

        // origin/main's PayloadShape before #4282 added the members, read off that build through
        // PayloadShapeForTests. Hardcoded: a value that moved with the live fingerprint would
        // keep planting the stale payload wherever the current shape points.
        const string ShapeBeforeThePartMembers = "de85054287039cf4";
        Assert.NotEqual(ShapeBeforeThePartMembers, BcAppSymbolCache.PayloadShapeForTests);

        // Cold: parse once to get a well-formed current payload to derive the stale one from.
        RecordPatches.AddBcAppPath(appPath);
        Assert.Equal("#Basic,#Suite", SilentPartArea());
        var currentPath = BcAppSymbolCache.CachePathForShapeForTests(
            appPath, contentHash, BcAppSymbolCache.CacheVersionForTests, BcAppSymbolCache.PayloadShapeForTests);
        Assert.True(File.Exists(currentPath), $"no cache entry at {currentPath}");

        // What a box that ran the old build holds: the same payload with none of the new members,
        // at the old shape's location — and nothing at the current one.
        var stale = JsonNode.Parse(File.ReadAllText(currentPath));
        StripNewMembers(stale);
        var stalePath = BcAppSymbolCache.CachePathForShapeForTests(
            appPath, contentHash, BcAppSymbolCache.CacheVersionForTests, ShapeBeforeThePartMembers);
        Assert.NotEqual(stalePath, currentPath);
        Directory.CreateDirectory(Path.GetDirectoryName(stalePath)!);
        File.WriteAllText(stalePath, stale!.ToJsonString());
        File.Delete(currentPath);

        BcAppSymbolCache.ResetProcessCacheForTests();
        RecordPatches.ResetForReload();
        var parsesBefore = BcAppSymbolCache.ParseInvocationCountForTests(appPath);
        RecordPatches.AddBcAppPath(appPath);
        Assert.Equal("#Basic,#Suite", SilentPartArea());
        Assert.Equal(parsesBefore + 1, BcAppSymbolCache.ParseInvocationCountForTests(appPath));
        Assert.True(File.Exists(currentPath));

        // Warm: served from the fresh entry, same answer, no further parse.
        BcAppSymbolCache.ResetProcessCacheForTests();
        RecordPatches.ResetForReload();
        var parsesWarm = BcAppSymbolCache.ParseInvocationCountForTests(appPath);
        RecordPatches.AddBcAppPath(appPath);
        Assert.Equal("#Basic,#Suite", SilentPartArea());
        Assert.Equal(parsesWarm, BcAppSymbolCache.ParseInvocationCountForTests(appPath));
    }
}
