// RecordPatchesWarmReloadExtensionIndexTests — proves #2478: on a warm --server (or
// --watch) process, the SECOND and every later per-request reset must re-merge precompiled
// tableextension fields, exactly as the first request did.
//
// Root cause (per #2478's own investigation, reproduced here at the mechanism level rather
// than through a spawned --server process against a real Base Application closure)
// ------------------------------------------------------------------------------------------
// BcRuntime.ResetForNewBundleReload() -> RecordPatches.ResetForReload() runs once per
// runTests request in server mode (Program.cs's RunAllBundlesForServer) and once per
// --watch iteration. That reset clears _parsedExtensionFields and sets
// _bcSymbolExtensionIndexBuilt = false, intending the extension merge to re-run on next
// use — but it leaves _bcSymbolTableIndex populated. The ONLY call site for
// EnsureBcSymbolExtensionIndex() sits inside EnsureBcSymbolTableIndex(), after that method's
// `if (_bcSymbolTableIndex != null) return;` guard — so once _bcSymbolTableIndex survives a
// reset, the extension merge never runs again for the rest of the process's life, and every
// metatable built from the second request on is missing its precompiled tableextension
// fields.
//
// Why a direct unit test, not a spawned --server process against real Base Application
// ------------------------------------------------------------------------------------------
// The upstream repro (Microsoft Tests-TestLibraries's dependency closure) needs a real BC
// service-tier artifact set and violates .claude/rules/no-base-app-in-csharp-tests.md if
// reproduced as a C# fixture's "application" dependency. RecordPatches.AddBcAppPath accepts
// ANY precompiled .app carrying a SymbolReference.json — the same synthetic in-process .app
// technique RecordPatchesPrecompiledTableExtEvictionTests.cs (#2126) already uses reproduces
// the EXACT defective call chain (EnsureBcSymbolTableIndex -> EnsureBcSymbolExtensionIndex)
// without any real BC dependency, so this test drives it directly, simulating two requests:
//   1. Reference an unrelated table so the symbol table + extension indexes get built once
//      ("request 1"), and confirm the base table's extension field resolves.
//   2. Call RecordPatches.ResetForReload() directly — the exact method the server calls
//      between requests — then re-register the SAME source dir AND the SAME .app, which is
//      what Program.cs does (2196 then 2354/2357 on the CLI path, 4049 then 4533/4534 on the
//      server path: the reset precedes the bundle's own dep registration in both).
//   3. Reference the unrelated table again ("request 2") and confirm the extension field
//      STILL resolves. Without the fix this throws "extension field 50 ... not found".
//
// #2755 CORRECTION. Step 2 used to skip the AddBcAppPath call, on the stated grounds that
// "_bcAppPaths is registered once at startup and is deliberately NOT re-added, matching
// production". That was wrong about production — Program.cs registers a bundle's resolved dep
// closure inside the per-bundle loop, AFTER the reset, on both paths — and #2755 made the
// error visible by clearing the per-bundle registrations in ResetForReload: this test then
// failed with "no NCLMetaTable for table 93911 (dependency source not parsed)" on CI legs 27.5
// and 28.4, the two that run AlRunner.Tests.
//
// Re-registering is production-faithful, but AddBcAppPath invalidates the indexes itself, so
// the end-to-end half below would now survive a reintroduced #2478 for the wrong reason. The
// #2478 claim is therefore ALSO asserted directly, against the reset's own post-state:
// ResetForReload must leave BOTH _bcSymbolTableIndex null AND _bcSymbolExtensionIndexBuilt
// false. That pair IS #2478 — the extension merge's only call site is inside
// EnsureBcSymbolTableIndex, behind its `_bcSymbolTableIndex != null` guard, so a reset that
// drops the flag but keeps the index short-circuits the merge forever.
using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// RecordPatchesSerialCollection, not BcEngineCollection: this class calls
// RecordPatches.ResetForReload() directly, which ParserStaticsIsolationGuardTests requires
// to be in RecordPatchesSerialCollection (the AL parse statics are process-wide, and xunit
// runs collections in parallel — see that guard's own header for #1696). Both
// RecordPatchesSerialCollection and BcEngineCollection set DisableParallelization = true,
// and xUnit runs every DisableParallelization collection serially relative to every OTHER
// one too (see CollectionCostOrderer.cs), so this still can't race a BcEngineCollection
// class. The BC engine bootstrap itself runs at [ModuleInitializer] time (BcEngineBootstrap,
// BcEngineCollection.cs), unconditionally, before any test — BcEngineFixture is only a
// convenience DI wrapper over BcEngineBootstrap.Ready/SkipReason, so reading those directly
// works identically without joining BcEngineCollection.
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class RecordPatchesWarmReloadExtensionIndexTests : IDisposable
{
    private readonly string _root;

    public RecordPatchesWarmReloadExtensionIndexTests()
    {
        _root = TestScratch.Dir("al-runner-2478-tests");
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

    [SkippableFact]
    public void ResetForReload_RebuildsPrecompiledExtensionMergeOnNextRequest()
    {
        TestArtifacts.SkipIf(!BcEngineBootstrap.Ready,
            BcEngineBootstrap.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // Object ids process-wide unique among AlRunner.Tests statics (shared _parsedTables /
        // _metaTableCache), and outside every other file's declared ranges — 939xx is used by
        // RecordPatchesPrecompiledTableExtEvictionTests.cs at 93900-93902; this file uses
        // 93910-93912.
        const int baseTableId = 93910;
        const string baseTableName = "Bug2478 Base";
        const int triggerTableId = 93911;
        const int extId = 93912;
        const int extFieldId = 50;
        const string extFieldName = "ExtField2478";

        var baseDir = Path.Combine(_root, "base");
        Directory.CreateDirectory(baseDir);
        File.WriteAllText(Path.Combine(baseDir, "Base.al"), $$"""
            table {{baseTableId}} "{{baseTableName}}"
            {
                fields
                {
                    field(1; "No."; Code[20]) { }
                }
                keys
                {
                    key(PK; "No.") { Clustered = true; }
                }
            }
            """);

        var sr = $$"""
            {
              "RuntimeVersion": "15.1",
              "Namespaces": [],
              "Tables": [
                {
                  "Id": {{triggerTableId}},
                  "Name": "Bug2478 Trigger",
                  "Fields": [
                    { "TypeDefinition": { "Name": "Code[20]" }, "Properties": [], "Id": 1, "Name": "No." }
                  ],
                  "Keys": [
                    { "Name": "PK", "FieldNames": [ "No." ] }
                  ]
                }
              ],
              "TableExtensions": [
                {
                  "TargetObject": "{{baseTableName}}",
                  "Fields": [
                    { "TypeDefinition": { "Name": "Code[10]" }, "Properties": [], "Id": {{extFieldId}}, "Name": "{{extFieldName}}" }
                  ],
                  "Id": {{extId}},
                  "Name": "Bug2478Ext"
                }
              ]
            }
            """;
        var appPath = Path.Combine(_root, "dep.app");
        WriteApp(appPath, sr);

        var skeleton = AlRunner.BcRuntime.SkeletonNCLMetadata;
        Assert.NotNull(skeleton);

        // ── REQUEST 1 ────────────────────────────────────────────────────────────────
        RecordPatches.AddSourceDir(baseDir);
        RecordPatches.AddBcAppPath(appPath);

        var trigger1 = RecordPatches.NCLMetadata_GetMetaTableById(skeleton!, triggerTableId, false, 0);
        Assert.Equal(triggerTableId, trigger1.TableId);

        var base1 = RecordPatches.EnsureTableInMetadataCache(baseTableId);
        Assert.NotNull(base1);
        var field1 = RecordPatches.NCLMetaTable_GetFieldByNoExt(base1!, extId, extFieldId);
        Assert.Equal(extFieldId, field1.FieldNo);
        Assert.Equal(extFieldName, field1.FieldName);

        // ── SIMULATE THE --server / --watch PER-REQUEST RESET ──────────────────────────
        // Exactly BcRuntime.ResetForNewBundleReload()'s delegate target.
        RecordPatches.ResetForReload();

        // [THEN, #2478 asserted at the mechanism] the reset dropped the built table index as
        // well as the extension-built flag. Before #2478's fix _bcSymbolTableIndex survived,
        // and because EnsureBcSymbolExtensionIndex is only ever called from inside
        // EnsureBcSymbolTableIndex — after its `if (_bcSymbolTableIndex != null) return;` —
        // the merge never ran again for the life of the process. Asserted here rather than
        // only end-to-end below, because the production-faithful re-registration on the next
        // line invalidates the indexes on its own and would mask a reintroduced #2478.
        Assert.Null(PrivateStatic("_bcSymbolTableIndex"));
        Assert.Equal(false, PrivateStatic("_bcSymbolExtensionIndexBuilt"));

        // What Program.cs does next: re-register this bundle's own source AND its resolved dep
        // closure. #2755 clears the per-bundle .app registrations in the reset above, so the
        // .app has to be re-added here exactly as production re-adds it.
        RecordPatches.AddSourceDir(baseDir);
        RecordPatches.AddBcAppPath(appPath);

        // ── REQUEST 2 ────────────────────────────────────────────────────────────────
        var trigger2 = RecordPatches.NCLMetadata_GetMetaTableById(skeleton!, triggerTableId, false, 0);
        Assert.Equal(triggerTableId, trigger2.TableId);

        var base2 = RecordPatches.EnsureTableInMetadataCache(baseTableId);
        Assert.NotNull(base2);
        // [THEN] the extension field is STILL present on request 2. Without the fix,
        // EnsureBcSymbolTableIndex's guard short-circuits before EnsureBcSymbolExtensionIndex
        // ever runs again, _parsedExtensionFields stays empty forever after the first reset,
        // and this throws "extension field 50 from extension 93912 not found".
        var field2 = RecordPatches.NCLMetaTable_GetFieldByNoExt(base2!, extId, extFieldId);
        Assert.Equal(extFieldId, field2.FieldNo);
        Assert.Equal(extFieldName, field2.FieldName);

        // ── ASSERT (negative): a genuinely nonexistent field still raises loudly on request 2 ──
        const int nonExistentFieldId = 999999;
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RecordPatches.NCLMetaTable_GetFieldByNoExt(base2!, extId, nonExistentFieldId));
        Assert.Contains($"extension field {nonExistentFieldId}", ex.Message);
        Assert.Contains("not found", ex.Message);
    }

    /// <summary>
    /// Read one of RecordPatches' private static index fields. Reflection rather than a
    /// production accessor: these are the internals #2478 is about, and a named accessor would
    /// invite callers. Throws with the field name if it is ever renamed, so the test reports
    /// "the probe is stale" instead of silently asserting null against a field that is gone.
    /// </summary>
    private static object? PrivateStatic(string field)
    {
        var f = typeof(RecordPatches).GetField(field,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException(
                $"RecordPatches.{field} is gone — this probe asserts #2478's reset invariant "
                + "directly and cannot do so against a renamed field. Re-point it, do not delete it.");
        return f.GetValue(null);
    }
}
