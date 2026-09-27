// #4783: registering a dependency .app drops the "absent" answers the table, page, report and
// xmlport metadata caches recorded before it, and leaves every built object where it is.
//
// The end-to-end proof is the ordered-bundle step in .github/workflows/bc-tests.yml
// (precompiled-dep-cache-null-first, then xmlport-precompiled-dep-metadata), which covers the
// table kind through AL. This class pins the mechanism for all four kinds, including the ones
// no committed fixture can reach from AL, and the half a live object must survive.
//
// Synthetic .app as in RecordPatchesBcAppSymbolReadFailureTests; no Base Application floor
// (.claude/rules/no-base-app-in-csharp-tests.md).

using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// RecordPatchesSerialCollection: this class mutates RecordPatches' process-wide caches and
// registration set, which the parser-statics guard requires to be serial (#1696).
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class AppRegistrationEvictsCachedNullsTests : IDisposable
{
    // Process-wide unique among AlRunner.Tests statics: 94783x is used only here.
    private const int AbsentId = 947830;
    private const int BuiltId = 947831;
    private const int AppTableId = 947832;
    private static readonly Guid Group = new("4f1c7d2a-9e35-4b86-a0d1-6c2e8b5f4783");
    private static readonly string[] Kinds = ["table", "page", "report", "xmlport"];

    private readonly string _root;

    public AppRegistrationEvictsCachedNullsTests()
    {
        _root = TestScratch.Dir("al-runner-4783-tests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var kind in Kinds)
        {
            var (processWide, byGroup) = RecordPatches.MetadataCachesForTests(kind);
            processWide.TryRemove(AbsentId, out _);
            processWide.TryRemove(BuiltId, out _);
            byGroup.TryRemove((Group, AbsentId), out _);
            byGroup.TryRemove((Group, BuiltId), out _);
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string WriteApp(string name)
    {
        var path = Path.Combine(_root, name);
        using (var zip = new FileStream(path, FileMode.Create))
        using (var za = new ZipArchive(zip, ZipArchiveMode.Create))
        {
            var entry = za.CreateEntry("SymbolReference.json");
            using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
            w.Write($$"""
                {
                  "RuntimeVersion": "15.1",
                  "Namespaces": [],
                  "Tables": [
                    {
                      "Id": {{AppTableId}},
                      "Name": "Bug4783 Registered Later",
                      "Fields": [ { "TypeDefinition": { "Name": "Code[20]" }, "Properties": [], "Id": 1, "Name": "No." } ],
                      "Keys": [ { "Name": "PK", "FieldNames": [ "No." ] } ]
                    }
                  ]
                }
                """);
        }
        return path;
    }

    [Theory]
    [InlineData("table")]
    [InlineData("page")]
    [InlineData("report")]
    [InlineData("xmlport")]
    public void AddBcAppPath_DropsCachedAbsence_AndKeepsBuiltObject(string kind)
    {
        var (processWide, byGroup) = RecordPatches.MetadataCachesForTests(kind);
        var built = new object();
        var builtInGroup = new object();

        // [GIVEN] an earlier lookup recorded AbsentId as absent, process-wide and for one app
        // group, and BuiltId holds a built object
        processWide[AbsentId] = null;
        byGroup[(Group, AbsentId)] = null;
        processWide[BuiltId] = built;
        byGroup[(Group, BuiltId)] = builtInGroup;

        // [WHEN] a dependency .app is registered
        RecordPatches.AddBcAppPath(WriteApp($"later-{kind}.app"));

        // [THEN] the absence is gone, so the next lookup asks the builder again
        Assert.False(processWide.ContainsKey(AbsentId),
            $"{kind}: a null cached before the .app was registered must not survive the registration");
        Assert.False(byGroup.ContainsKey((Group, AbsentId)),
            $"{kind}: a per-app-group null cached before the .app was registered must not survive it");

        // [THEN] the built object is the SAME instance: precompiled callers may hold it
        Assert.True(processWide.TryGetValue(BuiltId, out var afterBuilt));
        Assert.Same(built, afterBuilt);
        Assert.True(byGroup.TryGetValue((Group, BuiltId), out var afterBuiltInGroup));
        Assert.Same(builtInGroup, afterBuiltInGroup);
    }

    [Fact]
    public void AddBcAppPath_OfAnAlreadyRegisteredApp_LeavesTheCacheAlone()
    {
        var (processWide, _) = RecordPatches.MetadataCachesForTests("table");
        var app = WriteApp("registered-twice.app");
        RecordPatches.AddBcAppPath(app);

        // [GIVEN] an absence cached AFTER the .app is registered: it is the current answer
        processWide[AbsentId] = null;

        // [WHEN] the same path is registered again, which changes nothing
        RecordPatches.AddBcAppPath(app);

        // [THEN] the absence stays: only a registration that changes the set drops it
        Assert.True(processWide.TryGetValue(AbsentId, out var value));
        Assert.Null(value);
    }
}
