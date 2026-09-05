// RecordPatchesWarmReloadInstallBaselineTests — proves #2480: a warm --server (or --watch)
// process's install-baseline cache (TestExecutor._depCompanyBaselineCache, a PROCESS-lifetime
// dictionary that no per-request reset touches) must not restore a table's rows against a
// stale NCLMetaTable object captured on a PREVIOUS request.
//
// Root cause (per #2480's own investigation)
// ------------------------------------------------------------------------------------------
// RunAllBundlesForServer calls BcRuntime.ResetForNewBundleReload() per request, which clears
// RecordPatches._metaTableCache and _dataAccessByTable — so request 2 builds brand NEW
// NCLMetaTable instances for every table. TestExecutor._depCompanyBaselineCache sits OUTSIDE
// that reset (deliberately — it is the #1867 process-lifetime cache that makes a warm
// second app-group/request skip re-running dependency Install triggers). Its
// InstallBaselineSnapshot stores, per table, BaselineTable(TableId, MetaTable, Rows) with
// MetaTable being the NCLMetaTable object captured on request 1. RestoreInstallBaselineSnapshot
// used that captured object DIRECTLY — never re-resolving it against the CURRENT process
// epoch's live NCLMetadata caches — to construct the table's data access
// (`_mCreateTempDataAccess.Invoke(source, new[] { table.MetaTable })`) and every restored row's
// ReadOnlyRecordBuffer. #2478 is what usually makes a shape drift observable (a precompiled
// tableextension merge silently dropped on request 2), but the underlying defect — trusting a
// captured object reference across a request boundary the runtime has already invalidated — is
// real independent of #2478: any warm-reload scenario where a table's AL source genuinely
// changes shape between two requests hits it too, which is what this test forces directly.
//
// The on-disk install-baseline tier (RecordPatches.InstallBaselineDisk.cs,
// TryDeserializeInstallBaselineSnapshot) already re-resolves every table through
// EnsureTableInMetadataCache(tableId) and refuses to restore when FieldCount doesn't match —
// exactly the reconciliation the in-memory path was missing. RestoreInstallBaselineSnapshot now
// does the same: re-resolve, and throw loudly (.claude/rules/loud-failures.md) rather than
// silently restore rows against a shape it cannot reconcile — RestoreInstallBaselineSnapshot has
// no MISS-fallback path to recompute from, so a silent reconciliation attempt would risk
// misaligning field values instead of refusing outright.
//
// Why a direct unit test, not a spawned --server process against real dependency Install
// triggers writing rows
// ------------------------------------------------------------------------------------------
// A real end-to-end repro needs a genuinely PRECOMPILED dependency .app with an executable
// Install-trigger body — .claude/rules/no-base-app-in-csharp-tests.md forbids reaching for
// Base Application to get one, and hand-authoring a fresh compiled .app with real IL is a much
// larger undertaking than the defect requires proving. The defect is entirely about metadata
// object identity at restore time, independent of whether any row was ever install-trigger
// seeded, so this test drives RecordPatches' own snapshot/restore entry points directly (the
// same technique RecordPatchesWarmReloadExtensionIndexTests / #2478 and
// RecordPatchesPrecompiledTableExtEvictionTests / #2126 already use), forcing the exact
// "table shape changed between two --server requests" scenario rather than relying on #2478's
// specific tableextension-merge mechanism to manufacture the drift.
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

// RecordPatchesSerialCollection, not BcEngineCollection: this class calls
// RecordPatches.ResetForReload() directly, which ParserStaticsIsolationGuardTests requires
// to be in RecordPatchesSerialCollection (see that guard's own header for #1696 — the AL
// parse statics are process-wide, and xunit runs collections in parallel). Both
// RecordPatchesSerialCollection and BcEngineCollection set DisableParallelization = true,
// and xUnit runs every DisableParallelization collection serially relative to every OTHER
// one too (see CollectionCostOrderer.cs), so this still can't race a BcEngineCollection
// class. The BC engine bootstrap itself runs at [ModuleInitializer] time (BcEngineBootstrap,
// BcEngineCollection.cs), unconditionally, before any test — BcEngineFixture is only a
// convenience DI wrapper over BcEngineBootstrap.Ready/SkipReason, so reading those directly
// works identically without joining BcEngineCollection.
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class RecordPatchesWarmReloadInstallBaselineTests : IDisposable
{
    private readonly string _root;

    public RecordPatchesWarmReloadInstallBaselineTests()
    {
        _root = TestScratch.Dir("al-runner-2480-tests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static void WriteOneFieldTable(string dir, int tableId, string tableName)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "T.al"), $$"""
            table {{tableId}} "{{tableName}}"
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
    }

    private static void WriteTwoFieldTable(string dir, int tableId, string tableName)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "T.al"), $$"""
            table {{tableId}} "{{tableName}}"
            {
                fields
                {
                    field(1; "No."; Code[20]) { }
                    field(2; "Extra"; Code[10]) { }
                }
                keys
                {
                    key(PK; "No.") { Clustered = true; }
                }
            }
            """);
    }

    [SkippableFact]
    public void RestoreInstallBaselineSnapshot_ThrowsWhenTableShapeChangedAcrossReload()
    {
        TestArtifacts.SkipIf(!BcEngineBootstrap.Ready,
            BcEngineBootstrap.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // 93920-93921: process-wide unique among AlRunner.Tests statics — 939xx is also used
        // by RecordPatchesPrecompiledTableExtEvictionTests.cs (93900-93902) and
        // RecordPatchesWarmReloadExtensionIndexTests.cs (93910-93912).
        const int tableId = 93920;
        const string tableName = "Bug2480 Base";

        var dir = Path.Combine(_root, "neg");
        WriteOneFieldTable(dir, tableId, tableName);
        RecordPatches.AddSourceDir(dir);

        var v1 = RecordPatches.EnsureTableInMetadataCache(tableId);
        Assert.NotNull(v1);
        var fieldCount1 = v1!.FieldCount;

        var source = RecordPatches.ResolveSkeletonDataAccessSource();
        Assert.NotNull(source);

        // A snapshot exactly as TestExecutor._depCompanyBaselineCache would hold one: no rows
        // needed to prove the defect — RestoreInstallBaselineSnapshot binds the table's data
        // access to `table.MetaTable` unconditionally, before it ever looks at Rows.
        var snapshot = new RecordPatches.InstallBaselineSnapshot(
            new List<RecordPatches.BaselineSource>
            {
                new(source!, new List<RecordPatches.BaselineTable>
                {
                    new(tableId, v1, Array.Empty<NavValue[]>()),
                }),
            },
            IsolatedStorage: null, RecordLinks: null, AutoIncrement: null);

        // ── SIMULATE THE --server / --watch PER-REQUEST RESET ──────────────────────────
        // TestExecutor._depCompanyBaselineCache (holding `snapshot` above) is a process
        // static that NO reset touches — exactly like production between two runTests
        // requests to one warm server process.
        RecordPatches.ResetForReload();

        // Request 2's bundle load re-registers source — but the table's AL source genuinely
        // changed shape (a developer edited it between requests), so the CURRENT process
        // epoch's metatable for this id is now a different SHAPE, not just a different object.
        WriteTwoFieldTable(dir, tableId, tableName);
        RecordPatches.AddSourceDir(dir);
        var v2 = RecordPatches.EnsureTableInMetadataCache(tableId);
        Assert.NotNull(v2);
        Assert.NotEqual(fieldCount1, v2!.FieldCount); // sanity: the shape genuinely changed

        // [THEN] restoring the request-1 snapshot must refuse rather than silently bind the
        // live process to a stale-shaped NCLMetaTable object. Before the fix this does not
        // throw at all — RestoreInstallBaselineSnapshot used `table.MetaTable` (v1) directly.
        var ex = Assert.Throws<InvalidOperationException>(
            () => RecordPatches.RestoreInstallBaselineSnapshot(snapshot));
        Assert.Contains(tableId.ToString(), ex.Message);
        Assert.Contains(fieldCount1.ToString(), ex.Message);
        Assert.Contains(v2.FieldCount.ToString(), ex.Message);
    }

    [SkippableFact]
    public void RestoreInstallBaselineSnapshot_SucceedsWhenTableShapeUnchangedAcrossReload()
    {
        TestArtifacts.SkipIf(!BcEngineBootstrap.Ready,
            BcEngineBootstrap.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // Distinct id range from the negative fact above so the two tests' shared statics
        // (_parsedTables / _metaTableCache) cannot interfere with each other.
        const int tableId = 93921;
        const string tableName = "Bug2480 Base Stable";

        var dir = Path.Combine(_root, "pos");
        WriteOneFieldTable(dir, tableId, tableName);
        RecordPatches.AddSourceDir(dir);

        var v1 = RecordPatches.EnsureTableInMetadataCache(tableId);
        Assert.NotNull(v1);

        var source = RecordPatches.ResolveSkeletonDataAccessSource();
        Assert.NotNull(source);

        var snapshot = new RecordPatches.InstallBaselineSnapshot(
            new List<RecordPatches.BaselineSource>
            {
                new(source!, new List<RecordPatches.BaselineTable>
                {
                    new(tableId, v1, Array.Empty<NavValue[]>()),
                }),
            },
            IsolatedStorage: null, RecordLinks: null, AutoIncrement: null);

        RecordPatches.ResetForReload();

        // Request 2 re-registers the IDENTICAL table definition — the common warm-reload case
        // (only some OTHER file in the bundle changed). The object is rebuilt fresh either way
        // (ResetForReload always clears _metaTableCache), but the SHAPE is unchanged.
        WriteOneFieldTable(dir, tableId, tableName);
        RecordPatches.AddSourceDir(dir);
        var v2 = RecordPatches.EnsureTableInMetadataCache(tableId);
        Assert.NotNull(v2);
        Assert.Equal(v1!.FieldCount, v2!.FieldCount); // sanity: shape genuinely unchanged

        // [THEN] restore must still succeed — the fix re-resolves against the live cache
        // instead of refusing outright just because the OBJECT is not the one captured on
        // request 1.
        var restoreEx = Record.Exception(() => RecordPatches.RestoreInstallBaselineSnapshot(snapshot));
        Assert.Null(restoreEx);
    }
}
