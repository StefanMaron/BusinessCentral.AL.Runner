// NestedTableMaterialisationHydrationTests — the proving tests for issue #2877.
//
// THE DEFECT
//   Hydrating table X under --test-data runs BC's own metadata and NavValue construction, and
//   that code can reach a Record of another table Y and land straight back in
//   GetDataAccessForTableCore. A nested load there would recurse, so it is refused — but by the
//   time it is refused, Y's storage has already been created and published into perTable. The
//   design's own rule is "storage presence IS the have-we-loaded-this answer", so every later
//   touch of Y found the entry and never loaded it: Y silently kept NONE of its backup rows for
//   the rest of the run, and TestDataProvisioner recorded no outcome for it either, so the
//   summary could not report it and the store afterwards looked exactly like a table nothing
//   had ever touched.
//
// WHAT IS PROVED HERE
//   1. The debt is recorded against the storage INSTANCE the nested call published, and paid by
//      the next touch that is not itself inside a materialisation — same instance, backup rows
//      present.
//   2. It is paid ONCE, and a store nothing deferred is untouched by any of this.
//   3. When the store has been written to in the meantime the debt is WRITTEN OFF rather than
//      paid, because loading then would MIX the backup's rows with whatever put the others
//      there — and the write-off is reported, never silent. "Cannot tell" is written off too:
//      unknown is not empty (the same discipline RecordPatches.StoredTableCensus keeps).
//   4. Object Metadata (2000000071) is the one table where a populate follows the
//      materialisation, and hydration and synthesis cannot both win. Synthesis is HELD OFF
//      while a load is owed rather than done and later withdrawn, so the backup's real rows
//      still win — which is the precedence #2519/#2788 built that branch on.
//
// WHY THESE ARE RUNNER TESTS
//   Every claim is about the runner's own materialisation order under --test-data, a flag no CI
//   leg passes. None of it is expressible from AL: by design AL cannot tell a table materialised
//   on first touch from one present from the start, and it cannot observe another table's
//   hydration reaching back for its own. .claude/rules/bc-behavior-tests-go-upstream.md keeps
//   them here.
using System.Collections.Concurrent;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// RecordPatches.TestDataOnDemandLoader and the deferred-load notifiers are process-wide statics
// that TestDataLazyLoadPolicyTests also writes, so these join the collection that serialises it.
[Collection(BcCompilerSharedReferenceCollection.Name)]
public sealed class NestedTableMaterialisationHydrationTests : IDisposable
{
    private const int OuterTableId = 61030;   // the table whose hydration reaches for the other
    private const int NestedTableId = 61031;  // the one first materialised from inside it
    private const int ObjectMetadataTableId = 2000000071;
    private const string BackupRow = "row-from-the-backup";

    private readonly Action<object, int>? _previousLoader;
    private readonly Action<int>? _previousDeferred;
    private readonly Action<int, string>? _previousWriteOff;

    public NestedTableMaterialisationHydrationTests()
    {
        _previousLoader = RecordPatches.TestDataOnDemandLoader;
        _previousDeferred = RecordPatches.TestDataDeferredLoadNotifier;
        _previousWriteOff = RecordPatches.TestDataDeferredLoadWriteOffNotifier;
        RecordPatches.TestDataOnDemandLoader = null;
        RecordPatches.TestDataDeferredLoadNotifier = null;
        RecordPatches.TestDataDeferredLoadWriteOffNotifier = null;
    }

    public void Dispose()
    {
        RecordPatches.TestDataOnDemandLoader = _previousLoader;
        RecordPatches.TestDataDeferredLoadNotifier = _previousDeferred;
        RecordPatches.TestDataDeferredLoadWriteOffNotifier = _previousWriteOff;
    }

    /// <summary>Stand-in for the in-memory store behind a DataAccess — one object per table
    /// whose rows are visible to whoever holds it.</summary>
    private sealed class FakeStore
    {
        public ConcurrentQueue<string> Rows { get; } = new();
    }

    private static bool? HasRows(object store) => !((FakeStore)store).Rows.IsEmpty;

    // ── 1. The central claim ────────────────────────────────────────────────────────

    /// <summary>
    /// THE claim of #2877. The loader for the outer table reaches a Record of the nested one,
    /// which lands back in the materialisation at depth &gt; 0 and publishes storage that cannot
    /// be loaded there. The next touch of the nested table — outside any hydration — must come
    /// back with the SAME storage, now carrying the backup's rows.
    ///
    /// Before the fix that touch returned the published-but-empty store and no load ever ran
    /// again for the rest of the run, so `rowsOnTheNextTouch` was 0.
    /// </summary>
    [Fact]
    public void ATableFirstMaterialisedInsideAnotherTablesHydration_IsHydratedOnTheNextTouch()
    {
        var source = new object();
        var perTable = new ConcurrentDictionary<int, object>();
        object? nestedGot = null;
        var loads = 0;

        RecordPatches.TestDataOnDemandLoader = (src, id) =>
        {
            Interlocked.Increment(ref loads);
            if (id == OuterTableId)
            {
                // BC's metadata / NavValue construction reaching a Record of another table.
                nestedGot = RecordPatches.GetOrCreateHydratedDataAccessCore(
                    src, perTable, NestedTableId, static () => new FakeStore(), HasRows);
                return;
            }
            ((FakeStore)perTable[id]).Rows.Enqueue(BackupRow);
        };

        RecordPatches.GetOrCreateHydratedDataAccessCore(
            source, perTable, OuterTableId, static () => new FakeStore(), HasRows);

        // The nested call got storage, and it was empty at that moment — nothing changes there.
        Assert.NotNull(nestedGot);
        Assert.Empty(((FakeStore)nestedGot!).Rows);
        Assert.Same(perTable[NestedTableId], nestedGot);

        // The next independent touch pays the debt into that same storage.
        var later = RecordPatches.GetOrCreateHydratedDataAccessCore(
            source, perTable, NestedTableId, static () => new FakeStore(), HasRows);

        Assert.Same(nestedGot, later);
        Assert.Equal(new[] { BackupRow }, ((FakeStore)later).Rows.ToArray());
        Assert.Equal(2, loads);   // the outer one, then the deferred one — not a third
    }

    /// <summary>
    /// Paid once, not on every touch. A deferred load that re-ran would duplicate every backup
    /// row on the second access, which is a worse answer than the missing rows it replaced.
    /// </summary>
    [Fact]
    public void TheDeferredLoad_IsPaidExactlyOnce_AndLaterTouchesAreFastPathHits()
    {
        var source = new object();
        var perTable = new ConcurrentDictionary<int, object>();
        var loads = 0;

        RecordPatches.TestDataOnDemandLoader = (src, id) =>
        {
            Interlocked.Increment(ref loads);
            if (id == OuterTableId)
            {
                RecordPatches.GetOrCreateHydratedDataAccessCore(
                    src, perTable, NestedTableId, static () => new FakeStore(), HasRows);
                return;
            }
            ((FakeStore)perTable[id]).Rows.Enqueue(BackupRow);
        };

        RecordPatches.GetOrCreateHydratedDataAccessCore(
            source, perTable, OuterTableId, static () => new FakeStore(), HasRows);

        var first = RecordPatches.GetOrCreateHydratedDataAccessCore(
            source, perTable, NestedTableId, static () => new FakeStore(), HasRows);
        var second = RecordPatches.GetOrCreateHydratedDataAccessCore(
            source, perTable, NestedTableId, static () => new FakeStore(), HasRows);
        var third = RecordPatches.GetOrCreateHydratedDataAccessCore(
            source, perTable, NestedTableId, static () => new FakeStore(), HasRows);

        Assert.Same(first, second);
        Assert.Same(second, third);
        Assert.Equal(new[] { BackupRow }, ((FakeStore)third).Rows.ToArray());
        Assert.Equal(2, loads);
    }

    /// <summary>
    /// The debt names the storage INSTANCE, so a table materialised normally never carries one
    /// and the ordinary path is untouched. Without this, "always load again" would satisfy the
    /// test above and load every table twice.
    /// </summary>
    [Fact]
    public void ATableMaterialisedNormally_OwesNothing_AndIsNotLoadedTwice()
    {
        var source = new object();
        var perTable = new ConcurrentDictionary<int, object>();
        var loads = 0;
        RecordPatches.TestDataOnDemandLoader = (_, id) =>
        {
            Interlocked.Increment(ref loads);
            ((FakeStore)perTable[id]).Rows.Enqueue(BackupRow);
        };

        var first = RecordPatches.GetOrCreateHydratedDataAccessCore(
            source, perTable, NestedTableId, static () => new FakeStore(), HasRows);
        Assert.False(RecordPatches.IsAwaitingTestDataHydration(first));

        var second = RecordPatches.GetOrCreateHydratedDataAccessCore(
            source, perTable, NestedTableId, static () => new FakeStore(), HasRows);

        Assert.Same(first, second);
        Assert.Equal(1, loads);
        Assert.Equal(new[] { BackupRow }, ((FakeStore)second).Rows.ToArray());
    }

    /// <summary>
    /// The debt is observable from outside the materialisation while it stands, and gone once it
    /// is settled. That predicate is what the Object Metadata branch consults to decide whether
    /// synthesising now would shadow a load that is still owed.
    /// </summary>
    [Fact]
    public void AwaitingHydration_IsTrueForTheNestedStore_AndFalseOnceTheDebtIsSettled()
    {
        var source = new object();
        var perTable = new ConcurrentDictionary<int, object>();
        object? nestedGot = null;

        RecordPatches.TestDataOnDemandLoader = (src, id) =>
        {
            if (id == OuterTableId)
            {
                nestedGot = RecordPatches.GetOrCreateHydratedDataAccessCore(
                    src, perTable, NestedTableId, static () => new FakeStore(), HasRows);
                Assert.True(RecordPatches.IsAwaitingTestDataHydration(nestedGot));
                return;
            }
            ((FakeStore)perTable[id]).Rows.Enqueue(BackupRow);
        };

        RecordPatches.GetOrCreateHydratedDataAccessCore(
            source, perTable, OuterTableId, static () => new FakeStore(), HasRows);

        Assert.True(RecordPatches.IsAwaitingTestDataHydration(nestedGot!));

        var later = RecordPatches.GetOrCreateHydratedDataAccessCore(
            source, perTable, NestedTableId, static () => new FakeStore(), HasRows);

        Assert.Same(nestedGot, later);
        Assert.False(RecordPatches.IsAwaitingTestDataHydration(later));
    }

    // ── 3. The write-off: never MIX, and never silently ─────────────────────────────

    /// <summary>
    /// Something wrote into the store between the nested publication and the touch that would
    /// pay the debt. Loading now would put the backup's rows on top of those — the mixed result
    /// #2788 exists to prevent, wearing a different hat — so the debt is written off instead,
    /// with the table id and the reason reported. A silent skip is what produced #2877 in the
    /// first place.
    /// </summary>
    [Fact]
    public void WhenTheStoreWasWrittenToMeanwhile_TheDebtIsWrittenOffAndReported_NotPaidOnTop()
    {
        var source = new object();
        var perTable = new ConcurrentDictionary<int, object>();
        var writeOffs = new List<(int TableId, string Reason)>();
        RecordPatches.TestDataDeferredLoadWriteOffNotifier = (id, reason) => writeOffs.Add((id, reason));

        var loads = 0;
        RecordPatches.TestDataOnDemandLoader = (src, id) =>
        {
            Interlocked.Increment(ref loads);
            if (id == OuterTableId)
            {
                var nested = RecordPatches.GetOrCreateHydratedDataAccessCore(
                    src, perTable, NestedTableId, static () => new FakeStore(), HasRows);
                // The nested caller writes through the handle it was just given.
                ((FakeStore)nested).Rows.Enqueue("written-by-the-nested-caller");
                return;
            }
            ((FakeStore)perTable[id]).Rows.Enqueue(BackupRow);
        };

        RecordPatches.GetOrCreateHydratedDataAccessCore(
            source, perTable, OuterTableId, static () => new FakeStore(), HasRows);

        var later = RecordPatches.GetOrCreateHydratedDataAccessCore(
            source, perTable, NestedTableId, static () => new FakeStore(), HasRows);

        // The row that was there stays, and the backup's row is NOT stacked on top of it.
        Assert.Equal(new[] { "written-by-the-nested-caller" }, ((FakeStore)later).Rows.ToArray());
        Assert.Equal(1, loads);
        // …and it is reported, naming the table and saying why.
        var writeOff = Assert.Single(writeOffs);
        Assert.Equal(NestedTableId, writeOff.TableId);
        Assert.Contains("row", writeOff.Reason, StringComparison.OrdinalIgnoreCase);
        // Settled: a later touch does not re-try and does not report twice.
        RecordPatches.GetOrCreateHydratedDataAccessCore(
            source, perTable, NestedTableId, static () => new FakeStore(), HasRows);
        Assert.Single(writeOffs);
        Assert.False(RecordPatches.IsAwaitingTestDataHydration(later));
    }

    /// <summary>
    /// "Cannot tell" is written off too. A probe that cannot read BC's private store layout must
    /// not be read as "empty, go ahead and load" — that is the same false-negative
    /// RecordPatches.StoredTableCensus refuses to make, and here it would silently mix rows.
    /// </summary>
    [Fact]
    public void WhenTheStoreCannotBeProbed_TheDebtIsWrittenOffRatherThanPaidBlind()
    {
        var source = new object();
        var perTable = new ConcurrentDictionary<int, object>();
        var writeOffs = new List<(int TableId, string Reason)>();
        RecordPatches.TestDataDeferredLoadWriteOffNotifier = (id, reason) => writeOffs.Add((id, reason));

        static bool? CannotTell(object _) => null;

        var loads = 0;
        RecordPatches.TestDataOnDemandLoader = (src, id) =>
        {
            Interlocked.Increment(ref loads);
            if (id == OuterTableId)
            {
                RecordPatches.GetOrCreateHydratedDataAccessCore(
                    src, perTable, NestedTableId, static () => new FakeStore(), CannotTell);
                return;
            }
            ((FakeStore)perTable[id]).Rows.Enqueue(BackupRow);
        };

        RecordPatches.GetOrCreateHydratedDataAccessCore(
            source, perTable, OuterTableId, static () => new FakeStore(), CannotTell);
        var later = RecordPatches.GetOrCreateHydratedDataAccessCore(
            source, perTable, NestedTableId, static () => new FakeStore(), CannotTell);

        Assert.Empty(((FakeStore)later).Rows);
        Assert.Equal(1, loads);
        var writeOff = Assert.Single(writeOffs);
        Assert.Equal(NestedTableId, writeOff.TableId);
    }

    // ── 4. Object Metadata (2000000071): hydration and synthesis cannot both win ────

    /// <summary>
    /// The one table where a populate follows the materialisation. A nested first touch used to
    /// leave it published-and-unloaded AND let PopulateObjectMetadataSystemTable claim the
    /// once-per-provider flag and synthesise its 43 rows into it — so the deferred load would
    /// later land on top of synthesised rows, or (before this fix) never land at all.
    ///
    /// The answer: synthesis is held off while a load is owed. The nested caller sees an empty
    /// store for that moment, the next touch hydrates first, and the populate that follows finds
    /// rows and does nothing — which is the "real rows win, synthesis is the fallback"
    /// precedence the branch was built on.
    /// </summary>
    [Fact]
    public void ObjectMetadata_NestedFirstTouch_SynthesisesNothing_AndTheBackupsRowsStillWin()
    {
        var source = new object();
        var perTable = new ConcurrentDictionary<int, object>();
        var synthesised = 0;

        void Populate(object store)
        {
            // The real populator's contract: synthesise only into a store nobody has filled.
            if (!((FakeStore)store).Rows.IsEmpty) return;
            synthesised++;
            ((FakeStore)store).Rows.Enqueue("synthesised");
        }

        object? nestedGot = null;
        RecordPatches.TestDataOnDemandLoader = (src, id) =>
        {
            if (id == OuterTableId)
            {
                nestedGot = RecordPatches.MaterialiseObjectMetadataStoreCore(
                    src, perTable, ObjectMetadataTableId,
                    static () => new FakeStore(), HasRows, Populate);
                return;
            }
            ((FakeStore)perTable[id]).Rows.Enqueue(BackupRow);
        };

        RecordPatches.GetOrCreateHydratedDataAccessCore(
            source, perTable, OuterTableId, static () => new FakeStore(), HasRows);

        // Nothing was synthesised into a store that still owes a load.
        Assert.NotNull(nestedGot);
        Assert.Empty(((FakeStore)nestedGot!).Rows);
        Assert.Equal(0, synthesised);

        var later = RecordPatches.MaterialiseObjectMetadataStoreCore(
            source, perTable, ObjectMetadataTableId,
            static () => new FakeStore(), HasRows, Populate);

        Assert.Same(nestedGot, later);
        Assert.Equal(new[] { BackupRow }, ((FakeStore)later).Rows.ToArray());
        Assert.Equal(0, synthesised);
    }

    /// <summary>
    /// The fallback arm, and it is what stops the fix from simply disabling synthesis: with no
    /// load owed — a backup that does not offer 2000000071, or a run whose loader inserts
    /// nothing — the populate runs exactly as before and its rows are what the table holds.
    /// </summary>
    [Fact]
    public void ObjectMetadata_WithNothingOwed_StillSynthesises_AndOnlyOnce()
    {
        var source = new object();
        var perTable = new ConcurrentDictionary<int, object>();
        var synthesised = 0;

        void Populate(object store)
        {
            if (!((FakeStore)store).Rows.IsEmpty) return;
            synthesised++;
            ((FakeStore)store).Rows.Enqueue("synthesised");
        }

        // A loader that offers this table nothing — the common case for a backup without it.
        RecordPatches.TestDataOnDemandLoader = static (_, _) => { };

        var first = RecordPatches.MaterialiseObjectMetadataStoreCore(
            source, perTable, ObjectMetadataTableId, static () => new FakeStore(), HasRows, Populate);
        var second = RecordPatches.MaterialiseObjectMetadataStoreCore(
            source, perTable, ObjectMetadataTableId, static () => new FakeStore(), HasRows, Populate);

        Assert.Same(first, second);
        Assert.Equal(new[] { "synthesised" }, ((FakeStore)second).Rows.ToArray());
        Assert.Equal(1, synthesised);
    }

    /// <summary>
    /// A run without --test-data installs no loader, so nothing can ever be owed and the
    /// Object Metadata branch keeps its pre-#2877 shape exactly: create, populate, hand back.
    /// This is every corpus and runner-extras leg.
    /// </summary>
    [Fact]
    public void WithNoLoaderInstalled_NothingIsEverOwed_AndTheObjectMetadataPopulateStillRuns()
    {
        var source = new object();
        var perTable = new ConcurrentDictionary<int, object>();
        var synthesised = 0;
        Assert.Null(RecordPatches.TestDataOnDemandLoader);

        var store = RecordPatches.MaterialiseObjectMetadataStoreCore(
            source, perTable, ObjectMetadataTableId, static () => new FakeStore(), HasRows,
            s => { synthesised++; ((FakeStore)s).Rows.Enqueue("synthesised"); });

        Assert.False(RecordPatches.IsAwaitingTestDataHydration(store));
        Assert.Equal(1, synthesised);
        Assert.Equal(new[] { "synthesised" }, ((FakeStore)store).Rows.ToArray());
    }
}
