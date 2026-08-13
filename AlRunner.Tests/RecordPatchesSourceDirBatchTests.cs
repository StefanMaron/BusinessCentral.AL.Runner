// RecordPatchesSourceDirBatchTests — issue #1833.
//
// AlRunner.Patches.RecordPatches.AddSourceDir called PopulateNclMetadataCache() at the end
// of EVERY call. PopulateNclMetadataCache's own cost is driven by the TOTAL number of ids
// known so far — it rebuilds `_parsedTables.Keys.ToArray()` (and the Page/Report/Query/
// XmlPort equivalents) and walks the whole set with an idempotent skip-if-cached check —
// not by what the single dir just added. Program.cs's "register-source-dirs" stage calls
// AddSourceDir once per suite in a loop (38 suites in the `tests/runner-extras` bundle),
// so that stage was O(N) calls each doing O(total-ids-so-far) work: quadratic in N.
// Measured on that bundle: 16.33s (#1833's issue body).
//
// The fix adds RecordPatches.AddSourceDirs(IEnumerable<string>): parse every dir in the
// batch FIRST, then call PopulateNclMetadataCache() exactly ONCE over the complete set.
// AddSourceDir(string) now delegates to it with a single-element array, so its per-call-
// populate semantics are UNCHANGED for call sites that add one dir at a time interleaved
// with other work (e.g. Program.cs's sibling-dependency emit loop).
//
// What these tests pin
// ---------------------
//  * POSITIVE (mechanism) — batching N new dirs in one AddSourceDirs call performs the
//    cache pass exactly ONCE, not N times. A COUNT (PopulateNclMetadataCacheCallCount),
//    never a duration — see BcCompilerWarmLoaderReuseTests for the same discipline on
//    #1832.
//  * POSITIVE (content) — every dir's table is actually resolvable afterward via the SAME
//    path AL test code reaches at runtime (NCLMetadata_GetMetaTableById on the skeleton).
//    Batching only changes WHEN the cache pass runs, never what ends up in it — this is
//    what would catch an implementation that "batched" by silently dropping anything but
//    the last dir.
//  * NEGATIVE (the staleness hazard #1832 taught us to check for) — a genuinely NEW source
//    dir registered by itself AFTER the batch (mirroring the sibling-dependency loop, which
//    still calls AddSourceDir one dir at a time, sometimes after the batched
//    register-source-dirs stage has already run) still runs its OWN cache pass and its
//    table becomes resolvable. A memo that stopped re-populating once "the bundle looked
//    fully registered" would go undetected by the first two tests alone and would silently
//    drop a sibling dependency's tables — exactly the shape of the #1832 regression this
//    issue was flagged as sharing.
//  * A repeat AddSourceDirs call over dirs already registered (the existing _sourceDirs
//    de-dup) performs ZERO additional cache passes — proves the batch entry point doesn't
//    turn a no-op call into wasted work.

using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class RecordPatchesSourceDirBatchTests : IDisposable
{
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public RecordPatchesSourceDirBatchTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = Path.Combine(Path.GetTempPath(), "al-runner-source-dir-batch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string WriteTableDir(int tableId, string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{name}.al"), $$"""
            table {{tableId}} "SourceDirBatch {{name}}"
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
        return dir;
    }

    /// <summary>Resolves a table id the same way AL test code does at runtime — through the
    /// hooked NCLMetadata_GetMetaTableById on the real skeleton instance — rather than
    /// reaching into the private cache dictionary. Returns null instead of throwing so
    /// callers can assert absence too.</summary>
    private static NCLMetaTable? TryResolve(int tableId)
    {
        var skeleton = AlRunner.BcRuntime.SkeletonNCLMetadata;
        if (skeleton == null) return null;
        try
        {
            return RecordPatches.NCLMetadata_GetMetaTableById(skeleton, tableId, false, 0);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    [SkippableFact]
    public void AddSourceDirs_BatchesNDirsIntoOneCachePass_AndEveryDirsTableIsResolvable()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var dirs = new[]
        {
            WriteTableDir(93701, "A"),
            WriteTableDir(93702, "B"),
            WriteTableDir(93703, "C"),
            WriteTableDir(93704, "D"),
            WriteTableDir(93705, "E"),
        };

        var before = RecordPatches.PopulateNclMetadataCacheCallCount;
        RecordPatches.AddSourceDirs(dirs);
        var afterBatch = RecordPatches.PopulateNclMetadataCacheCallCount;

        // Mechanism: one call, one cache pass — NOT one pass per dir. This is the direct
        // fix for #1833's quadratic register-source-dirs stage (16.33s for 38 dirs).
        Assert.Equal(1, afterBatch - before);

        // Content: batching must not drop anything. Every one of the 5 dirs' tables is
        // resolvable through the real runtime lookup path, with its own id echoed back —
        // an implementation that only processed the LAST dir passed to AddSourceDirs would
        // fail this (93701-93704 would come back null) while still passing the count
        // assertion above.
        Assert.Equal(93701, TryResolve(93701)?.TableId);
        Assert.Equal(93702, TryResolve(93702)?.TableId);
        Assert.Equal(93703, TryResolve(93703)?.TableId);
        Assert.Equal(93704, TryResolve(93704)?.TableId);
        Assert.Equal(93705, TryResolve(93705)?.TableId);

        // Repeating the SAME batch (every dir already registered) is a true no-op: zero
        // additional cache passes. Proves the batch entry point routes through the existing
        // _sourceDirs de-dup instead of unconditionally re-populating.
        RecordPatches.AddSourceDirs(dirs);
        Assert.Equal(afterBatch, RecordPatches.PopulateNclMetadataCacheCallCount);
    }

    [SkippableFact]
    public void AddSourceDir_AfterABatch_StillSeesAGenuinelyNewDirsTable()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // Mirrors Program.cs's sibling-dependency loop: the bundle-level batch registers
        // its own suites first, and a LATER, SEPARATE single AddSourceDir call (a resolved
        // dependency's source dir) must still get its own cache pass — nothing is allowed
        // to assume "the cache looks fully populated already" and skip real new work. This
        // is the #1832-shaped hazard #1833 was flagged as sharing: a memo that stops
        // invalidating on genuinely new input would pass every test above and still be
        // wrong here.
        var batchDirs = new[]
        {
            WriteTableDir(93710, "F"),
            WriteTableDir(93711, "G"),
        };
        RecordPatches.AddSourceDirs(batchDirs);

        var lateDir = WriteTableDir(93712, "Late");
        var beforeLate = RecordPatches.PopulateNclMetadataCacheCallCount;
        RecordPatches.AddSourceDir(lateDir);
        var afterLate = RecordPatches.PopulateNclMetadataCacheCallCount;

        // The late, standalone dir triggers its own cache pass...
        Assert.Equal(1, afterLate - beforeLate);
        // ...and its table is genuinely visible, not silently dropped because the batch
        // "already ran".
        Assert.Equal(93712, TryResolve(93712)?.TableId);
        // The earlier batch's tables are unaffected — still resolvable.
        Assert.Equal(93710, TryResolve(93710)?.TableId);
        Assert.Equal(93711, TryResolve(93711)?.TableId);

        // A table id that was never registered anywhere is still correctly "not found" —
        // guards against a broken implementation that answers something for every id.
        Assert.Null(TryResolve(93799));
    }
}
