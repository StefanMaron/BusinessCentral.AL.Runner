// RecordPatchesPrecompiledTableExtEvictionTests — proves #2126: the precompiled-.app
// tableextension merge path must evict an already-built NCLMetaTable for the base table,
// exactly like the AL-source tableextension parser already does.
//
// The asymmetry
// -------------
// Two writers merge tableextension fields into the shared _parsedExtensionFields
// dictionary:
//   - TryParseTableExtensionFile (RecordPatches.AlSourceParser.cs) — the AL-source path.
//   - EnsureBcSymbolExtensionIndex (RecordPatches.BcAppFallback.cs) — the precompiled-.app
//     symbol path.
// BuildNCLMetaTable only ever consults _parsedExtensionFields at BUILD time. If a base
// table's NCLMetaTable was already built and cached before one of these writers ran, that
// cached instance is frozen without the extension's fields forever, unless something evicts
// it from _metaTableCache so the next lookup rebuilds. The AL-source path always did this
// (EvictCachedMetaTableForBaseTable); the precompiled path never did.
//
// Why a direct unit test, not an AL fixture
// ------------------------------------------
// #2125's investigation built 2-hop and 3-hop dependency-chain AL fixtures and could not
// force the ordering that exposes the gap: in every fixture, nothing referenced the base
// table before EnsureBcSymbolExtensionIndex ran, so eviction had nothing stale to evict.
// This test drives RecordPatches' own public entry points directly, in the exact order the
// issue names, so the ordering is forced rather than hoped for:
//   1. Parse the base table from AL source and materialize its NCLMetaTable FIRST (before
//      any tableextension is known) via NCLMetadata_GetMetaTableById.
//   2. Register a precompiled .app whose SymbolReference.json carries a tableextension for
//      that SAME base table, plus an unrelated table only known via symbols.
//   3. Look up the unrelated table — its miss against _parsedTables walks the .app symbol
//      fallback (TryPopulateParsedTableFromBcApps -> EnsureBcSymbolTableIndex), which
//      co-builds EnsureBcSymbolExtensionIndex as a side effect. This is the real trigger a
//      large dependency graph exercises the moment ANY OTHER precompiled table is touched
//      after the base table was already referenced — see #2126's "why it is likely
//      reachable in a real workspace".
//   4. Read the extension field back off the base table's NCLMetaTable.
//
// Without the fix: step 4 throws "extension field 50 ... not found" — the NCLMetaTable
// cached in step 1 is untouched. With the fix: the merge in step 3 evicts it, so step 4's
// lookup rebuilds and finds the field.
using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class RecordPatchesPrecompiledTableExtEvictionTests : IDisposable
{
    private readonly BcEngineFixture _engine;
    private readonly string _root;

    public RecordPatchesPrecompiledTableExtEvictionTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = Path.Combine(Path.GetTempPath(), "al-runner-2126-tests", Guid.NewGuid().ToString("N"));
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
    public void PrecompiledTableExtensionMerge_EvictsAlreadyBuiltBaseMetaTable()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // Object ids picked to be process-wide unique among AlRunner.Tests statics (these
        // land in the same static _parsedTables / _metaTableCache the whole test assembly
        // shares) and outside every other file's declared ranges.
        const int baseTableId = 93900;
        const string baseTableName = "Bug2126 Base";
        const int triggerTableId = 93901;
        const int extId = 93902;
        const int extFieldId = 50;
        const string extFieldName = "ExtField2126";

        // ── ARRANGE: base table via AL source ───────────────────────────────────────────
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
        RecordPatches.AddSourceDir(baseDir);

        var skeleton = AlRunner.BcRuntime.SkeletonNCLMetadata;
        Assert.NotNull(skeleton);

        // ── STEP 1: materialize the base table's NCLMetaTable FIRST ─────────────────────
        // Nothing about tableextension 93902 is known to the runner yet.
        var before = RecordPatches.NCLMetadata_GetMetaTableById(skeleton!, baseTableId, false, 0);
        Assert.NotNull(before);
        Assert.Equal(baseTableId, before.TableId);
        Assert.False(before.TryGetFieldByNo(extFieldId, out _),
            "sanity check: the extension field must not exist on the pre-merge NCLMetaTable");

        // ── STEP 2: register a precompiled .app with a tableextension for the SAME base
        //    table, plus an unrelated table only known via symbols ──────────────────────
        var sr = $$"""
            {
              "RuntimeVersion": "15.1",
              "Namespaces": [],
              "Tables": [
                {
                  "Id": {{triggerTableId}},
                  "Name": "Bug2126 Trigger",
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
                  "Name": "Bug2126Ext"
                }
              ]
            }
            """;
        var appPath = Path.Combine(_root, "dep.app");
        WriteApp(appPath, sr);
        RecordPatches.AddBcAppPath(appPath);

        // ── STEP 3: reference the UNRELATED trigger table ───────────────────────────────
        // Its miss against _parsedTables walks the .app symbol fallback
        // (TryPopulateParsedTableFromBcApps -> EnsureBcSymbolTableIndex), which co-builds
        // EnsureBcSymbolExtensionIndex — merging the base table's tableextension fields into
        // _parsedExtensionFields as a side effect, exactly as it would in a real dependency
        // graph the moment any OTHER precompiled table gets referenced.
        var triggerMeta = RecordPatches.NCLMetadata_GetMetaTableById(skeleton!, triggerTableId, false, 0);
        Assert.NotNull(triggerMeta);
        Assert.Equal(triggerTableId, triggerMeta.TableId);

        // ── STEP 4 / ASSERT (positive): the extension field now resolves ────────────────
        // Re-resolve through the same cache-or-build entry point BuildNCLMetaTable's callers
        // use. Without the fix this returns the STALE instance from step 1 (still missing
        // the field); with the fix the merge in step 3 evicted it, so this rebuilds.
        var after = RecordPatches.EnsureTableInMetadataCache(baseTableId);
        Assert.NotNull(after);
        var field = RecordPatches.NCLMetaTable_GetFieldByNoExt(after!, extId, extFieldId);
        Assert.Equal(extFieldId, field.FieldNo);
        Assert.Equal(extFieldName, field.FieldName);

        // ── ASSERT (negative): a genuinely nonexistent field still raises loudly ────────
        // The fix must not weaken this path into silently returning null/default.
        const int nonExistentFieldId = 999999;
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RecordPatches.NCLMetaTable_GetFieldByNoExt(after!, extId, nonExistentFieldId));
        Assert.Contains($"extension field {nonExistentFieldId}", ex.Message);
        Assert.Contains("not found", ex.Message);
    }
}
