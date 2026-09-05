// RecordPatchesBcAppSymbolReadFailureTests — proves #2712 at the caller: a registered
// dependency .app whose table extensions cannot be parsed to completion is a loud, typed
// failure at BOTH points RecordPatches reads it — never a shorter extension list.
//
// The two read points
// -------------------
// 1. Registration (RecordPatches.AddBcAppPath, called from Program.cs for every resolved
//    dependency): reads the .app's symbols eagerly. A parse failure here throws
//    BcAppSymbolReadException, which Program.cs turns into `FATAL: ... exit 1` before any
//    test runs, and the .app is NOT left registered.
// 2. The lazy index rebuild (EnsureBcSymbolTableIndex -> EnsureBcSymbolExtensionIndex), which
//    re-parses when the file changed on disk after registration — the --watch / --server
//    shape, where a dependency .app is rebuilt between iterations. Before the fix this path
//    set _bcSymbolExtensionIndexBuilt = true FIRST, swallowed the failure into a
//    `[RecordPatches]`-tagged stderr line (dropped by Log's default-verbosity filter), and
//    merged whatever partial list the parse had returned.
//
// Fixture: a synthetic .app whose SymbolReference.json has a real Tables[] entry (so the
// table-symbol read succeeds) plus a TableExtensions[] entry with a FIELD whose "Id" is a
// string, which makes JsonElement.TryGetInt32 throw after the earlier entry was collected — see
// BcAppSymbolCachePartialParseTests for why this reproduces the reported OOM shape exactly.
// Same synthetic-.app technique as RecordPatchesWarmReloadExtensionIndexTests (#2478); no
// Base Application floor (.claude/rules/no-base-app-in-csharp-tests.md).

using System.IO.Compression;
using System.Text;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// RecordPatchesSerialCollection: this class calls RecordPatches.ResetForReload() directly,
// which ParserStaticsIsolationGuardTests requires to be in this collection (#1696) — see
// RecordPatchesWarmReloadExtensionIndexTests's header for the full reasoning.
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class RecordPatchesBcAppSymbolReadFailureTests : IDisposable
{
    private readonly string _root;

    public RecordPatchesBcAppSymbolReadFailureTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-2712-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static void WriteApp(string path, string symbolReferenceJson)
    {
        using var zip = new FileStream(path, FileMode.Create);
        using var za = new ZipArchive(zip, ZipArchiveMode.Create);
        var entry = za.CreateEntry("SymbolReference.json");
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(symbolReferenceJson);
    }

    // Object ids process-wide unique among AlRunner.Tests statics (shared _parsedTables):
    // 939xx is used by RecordPatchesPrecompiledTableExtEvictionTests (93900-93902),
    // RecordPatchesWarmReloadExtensionIndexTests (93910-93912) and
    // RecordPatchesWarmReloadInstallBaselineTests (93920-93921); this file uses 93930-93935.
    private static string SymbolReference(int tableId, string tableName, int extId, bool poison)
    {
        var extensions = poison
            ? $$"""
              { "TargetObject": "{{tableName}}", "Fields": [ { "TypeDefinition": { "Name": "Code[10]" }, "Properties": [], "Id": 50, "Name": "ExtField2712" } ], "Id": {{extId}}, "Name": "Bug2712Ext" },
              { "TargetObject": "{{tableName}}", "Fields": [ { "TypeDefinition": { "Name": "Code[10]" }, "Properties": [], "Id": "not-a-number", "Name": "PoisonField" } ], "Id": {{extId + 1}}, "Name": "Bug2712Poison" }
              """
            : $$"""
              { "TargetObject": "{{tableName}}", "Fields": [ { "TypeDefinition": { "Name": "Code[10]" }, "Properties": [], "Id": 50, "Name": "ExtField2712" } ], "Id": {{extId}}, "Name": "Bug2712Ext" }
              """;
        return $$"""
            {
              "RuntimeVersion": "15.1",
              "Namespaces": [],
              "Tables": [
                {
                  "Id": {{tableId}},
                  "Name": "{{tableName}}",
                  "Fields": [ { "TypeDefinition": { "Name": "Code[20]" }, "Properties": [], "Id": 1, "Name": "No." } ],
                  "Keys": [ { "Name": "PK", "FieldNames": [ "No." ] } ]
                }
              ],
              "TableExtensions": [ {{extensions}} ]
            }
            """;
    }

    [Fact]
    public void AddBcAppPath_TableExtensionParseFailure_ThrowsAndDoesNotRegister()
    {
        const int tableId = 93930;
        const string tableName = "Bug2712 Unregistered";
        var appPath = Path.Combine(_root, "poison.app");
        WriteApp(appPath, SymbolReference(tableId, tableName, extId: 93931, poison: true));

        // [WHEN] Program.cs registers the dependency
        // [THEN] registration fails loudly with the typed exception naming the .app ...
        var ex = Assert.Throws<BcAppSymbolReadException>(() => RecordPatches.AddBcAppPath(appPath));
        Assert.Equal(appPath, ex.AppPath);
        Assert.Contains("poison.app", ex.Message);
        Assert.Contains("table extensions", ex.Message);

        // ... and the .app was NOT left registered: its table is unknown to the symbol index.
        // (Had it been registered, this lookup would build the index over it and find the id.)
        Assert.Equal(-1, RecordPatches.ResolveTableIdByName(tableName));
    }

    [Fact]
    public void AddBcAppPath_CompleteParse_RegistersAndResolves()
    {
        const int tableId = 93932;
        const string tableName = "Bug2712 Registered";
        var appPath = Path.Combine(_root, "good.app");
        WriteApp(appPath, SymbolReference(tableId, tableName, extId: 93933, poison: false));

        RecordPatches.AddBcAppPath(appPath);

        // Positive control: the eager read does not get in the way of a healthy .app.
        Assert.Equal(tableId, RecordPatches.ResolveTableIdByName(tableName));
    }

    [Fact]
    public void LazyRebuild_AppChangedToUnparseableOnDisk_ThrowsInsteadOfMergingPartial()
    {
        const int tableId = 93934;
        const string tableName = "Bug2712 Rebuilt";
        const int extId = 93935;
        var appPath = Path.Combine(_root, "rebuilt.app");
        WriteApp(appPath, SymbolReference(tableId, tableName, extId, poison: false));

        // [GIVEN] a healthy registration whose indexes have been built once
        RecordPatches.AddBcAppPath(appPath);
        Assert.Equal(tableId, RecordPatches.ResolveTableIdByName(tableName));

        // [WHEN] the .app is rebuilt on disk into a shape whose extension parse fails part-way,
        // and the per-request reset drops the indexes (exactly what --server/--watch do).
        // A different length + a bumped mtime give both BcAppSymbolCache caches a new key,
        // so the next index build re-parses rather than serving the earlier good result.
        WriteApp(appPath, SymbolReference(tableId, tableName, extId, poison: true));
        File.SetLastWriteTimeUtc(appPath, File.GetLastWriteTimeUtc(appPath).AddSeconds(5));
        RecordPatches.ResetForReload();

        // [THEN] the rebuild throws — before the fix it merged the one good extension, flagged
        // the index as built, printed a filtered-out stderr line and returned the id.
        var ex = Assert.Throws<BcAppSymbolReadException>(() => RecordPatches.ResolveTableIdByName(tableName));
        Assert.Contains("rebuilt.app", ex.Message);

        // [THEN] the failure is not turned into "built, nothing merged" either: asking again
        // re-attempts the build and fails the same loud way (the table index was not published
        // with the extension flag left false — the #2478 short-circuit shape).
        Assert.Throws<BcAppSymbolReadException>(() => RecordPatches.ResolveTableIdByName(tableName));

        // Cleanup for later tests in this collection: restore a parseable file so the still-
        // registered path does not fail every subsequent index rebuild in this process.
        WriteApp(appPath, SymbolReference(tableId, tableName, extId, poison: false));
        File.SetLastWriteTimeUtc(appPath, File.GetLastWriteTimeUtc(appPath).AddSeconds(10));
        RecordPatches.ResetForReload();
        Assert.Equal(tableId, RecordPatches.ResolveTableIdByName(tableName));
    }
}
