// TableMaterialisationOrderingTests — the proving tests for issue #2788.
//
// WHAT IS UNDER TEST
//   RecordPatches.GetOrCreateHydratedDataAccessCore is the create -> hydrate -> hand-out step
//   that GetDataAccessForTableCore's Object Metadata branch and its generic path both run when
//   a table's in-memory storage does not exist yet. Under --test-data a hydration (the
//   on-demand backup load, #2262) sits in the middle of it, and the rule the Object Metadata
//   branch depends on is: a backup's real rows win over the synthesised fallback. That rule
//   only holds if nobody can be handed the storage while the hydration is still running.
//
// WHY THESE ARE RUNNER TESTS AND NOT CORPUS TESTS
//   Every claim below is about the runner's own materialisation order across two threads under
//   a flag (--test-data) that no CI leg passes and that needs a ~1 GB SQL backup. None of it is
//   a statement about what Business Central does with AL source, and none of it is expressible
//   from AL: AL cannot start a second thread, cannot observe another thread's hand-out, and by
//   design cannot tell a table materialised on first touch from one present from the start.
//   .claude/rules/bc-behavior-tests-go-upstream.md therefore keeps them here.
//
// WHY THE INTERLEAVING IS FORCED, NOT HOPED FOR
//   A Task.Run pair with no synchronisation would be a flaky test, not a proving test, since
//   the defect's window is microseconds wide. So the loader stub PARKS inside the hydration and
//   the second thread is only started once it is parked. The winner is released by the loser's
//   own observation, so on the defective ordering the loser reads the store while the hydration
//   is provably still in flight, every run. On the fixed ordering the loser never gets to
//   observe anything before its wait ends, so that release never comes and the stub's bounded
//   wait expires instead — the expiry is the mechanism that lets the test finish, never the
//   thing being asserted.
using System.Collections.Concurrent;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// RecordPatches.TestDataOnDemandLoader is a process-wide static that TestDataLazyLoadPolicyTests
// also writes, so these tests join the collection that serialises it.
[Collection(BcCompilerSharedReferenceCollection.Name)]
public sealed class TableMaterialisationOrderingTests : IDisposable
{
    private const int TableId = 2000000071;      // Object Metadata — the branch that motivated #2788
    private const int OtherTableId = 2000000001; // any second table; only its id matters here
    private const string BackupRow = "row-from-the-backup";

    private readonly Action<object, int>? _previousLoader;

    public TableMaterialisationOrderingTests()
    {
        _previousLoader = RecordPatches.TestDataOnDemandLoader;
        RecordPatches.TestDataOnDemandLoader = null;
    }

    public void Dispose() => RecordPatches.TestDataOnDemandLoader = _previousLoader;

    /// <summary>A stand-in for the in-memory store behind a DataAccess. What matters is only
    /// that it is one object per table whose rows are visible to whoever holds it.</summary>
    private sealed class FakeStore
    {
        public ConcurrentQueue<string> Rows { get; } = new();
    }

    /// <summary>
    /// THE claim of #2788. Thread A wins the GetOrAdd and parks inside the --test-data
    /// hydration; thread B asks for the same table. B must not be handed the storage until A's
    /// hydration has finished, because the very next thing the Object Metadata branch does with
    /// what it is handed is decide whether to synthesise rows into it — and an empty-looking
    /// store makes it synthesise over rows that are about to arrive.
    ///
    /// Against the pre-fix ordering B is handed the store immediately and reads 0 rows.
    /// </summary>
    [Fact]
    public void TheLoserOfTheRace_IsNotHandedTheStoreUntilHydrationHasFinished()
    {
        var source = new object();
        var perTable = new ConcurrentDictionary<int, object>();
        var creations = 0;
        Func<object> create = () => { Interlocked.Increment(ref creations); return new FakeStore(); };

        using var winnerIsHydrating = new ManualResetEventSlim(false);
        using var loserHasReadTheStore = new ManualResetEventSlim(false);

        RecordPatches.TestDataOnDemandLoader = (_, id) =>
        {
            winnerIsHydrating.Set();
            // Released by the loser's read — which is exactly the read that must not be
            // possible. When the ordering is right this expires instead, and the test's
            // assertions are on what the loser saw, never on the timing.
            loserHasReadTheStore.Wait(TimeSpan.FromSeconds(2));
            // The real loader inserts through perTable's own entry (HydrateTestDataTable does
            // its own GetOrAdd on it), so the stub does the same.
            ((FakeStore)perTable[id]).Rows.Enqueue(BackupRow);
        };

        object? winnerGot = null;
        object? loserGot = null;
        var rowsTheLoserSaw = -1;

        var winner = Task.Run(() =>
            winnerGot = RecordPatches.GetOrCreateHydratedDataAccessCore(source, perTable, TableId, create));

        Assert.True(winnerIsHydrating.Wait(TimeSpan.FromSeconds(30)),
            "the winner never reached the hydration, so no race was staged");

        var loser = Task.Run(() =>
        {
            loserGot = RecordPatches.GetOrCreateHydratedDataAccessCore(source, perTable, TableId, create);
            rowsTheLoserSaw = ((FakeStore)loserGot).Rows.Count;
            loserHasReadTheStore.Set();
        });

        Assert.True(Task.WaitAll(new[] { winner, loser }, TimeSpan.FromSeconds(60)),
            "a materialisation never completed");

        // The whole claim: what the loser was handed already carried the backup's row.
        Assert.Equal(1, rowsTheLoserSaw);
        Assert.Equal(new[] { BackupRow }, ((FakeStore)loserGot!).Rows.ToArray());

        // …and it is the same storage the winner hydrated, created exactly once. A "fix" that
        // handed the loser a second, private store would satisfy the row count and be wrong.
        Assert.Same(winnerGot, loserGot);
        Assert.Same(perTable[TableId], loserGot);
        Assert.Equal(1, creations);
    }

    /// <summary>
    /// The same claim, but on the SECOND materialisation of the same (source, table) — which is
    /// the one that actually repeats in a run. ResetPerTestState() drains every source's perTable
    /// (RecordPatches.cs) at bundle start (TestExecutor) and at every install-baseline boundary
    /// restore (RestoreInstallBaseline), so a table is materialised again and again, not once.
    ///
    /// This models that reset exactly: materialise normally, drop the entry the way the reset
    /// does, then re-run the race. With a latch that only remembers "this (source, table) has
    /// been materialised at some point", the second race is unguarded — the fast path sees a
    /// stale-true latch and an entry the winner published before entering the loader, and hands
    /// the loser a store that is present-and-empty. The latch has to name the store instance it
    /// was set for, so that a re-created store invalidates it.
    /// </summary>
    [Fact]
    public void AfterAPerTestReset_TheLoserOfTheRaceStillWaitsForHydration()
    {
        var source = new object();
        var perTable = new ConcurrentDictionary<int, object>();

        // ── First materialisation, uncontended: this is what sets the latch. ──
        RecordPatches.TestDataOnDemandLoader =
            (_, id) => ((FakeStore)perTable[id]).Rows.Enqueue(BackupRow);
        var beforeReset = RecordPatches.GetOrCreateHydratedDataAccessCore(
            source, perTable, TableId, static () => new FakeStore());
        Assert.Equal(new[] { BackupRow }, ((FakeStore)beforeReset).Rows.ToArray());

        // ── ResetPerTestState(): drains the per-source dictionary, touches nothing else. ──
        perTable.Clear();

        // ── Second materialisation, raced exactly as the first test races the first one. ──
        var creations = 0;
        Func<object> create = () => { Interlocked.Increment(ref creations); return new FakeStore(); };

        using var winnerIsHydrating = new ManualResetEventSlim(false);
        using var loserHasReadTheStore = new ManualResetEventSlim(false);

        RecordPatches.TestDataOnDemandLoader = (_, id) =>
        {
            winnerIsHydrating.Set();
            loserHasReadTheStore.Wait(TimeSpan.FromSeconds(2));
            ((FakeStore)perTable[id]).Rows.Enqueue(BackupRow);
        };

        object? winnerGot = null;
        object? loserGot = null;
        var rowsTheLoserSaw = -1;

        var winner = Task.Run(() =>
            winnerGot = RecordPatches.GetOrCreateHydratedDataAccessCore(source, perTable, TableId, create));

        Assert.True(winnerIsHydrating.Wait(TimeSpan.FromSeconds(30)),
            "the winner never reached the second hydration, so no race was staged");

        var loser = Task.Run(() =>
        {
            loserGot = RecordPatches.GetOrCreateHydratedDataAccessCore(source, perTable, TableId, create);
            rowsTheLoserSaw = ((FakeStore)loserGot).Rows.Count;
            loserHasReadTheStore.Set();
        });

        Assert.True(Task.WaitAll(new[] { winner, loser }, TimeSpan.FromSeconds(60)),
            "a materialisation never completed after the reset");

        Assert.Equal(1, rowsTheLoserSaw);
        Assert.Equal(new[] { BackupRow }, ((FakeStore)loserGot!).Rows.ToArray());

        // The post-reset store, freshly built and hydrated once — not the pre-reset one, and
        // not a second private copy handed to the loser.
        Assert.Same(winnerGot, loserGot);
        Assert.Same(perTable[TableId], loserGot);
        Assert.NotSame(beforeReset, loserGot);
        Assert.Equal(1, creations);
    }

    /// <summary>
    /// The reset seam without a race. This one does NOT fail on the pre-fix ordering — the
    /// single-threaded path already rebuilds and reloads, because a dropped entry fails the fast
    /// path's perTable probe. It is here to pin the two ways the fix could overshoot: an
    /// instance-scoped latch that never matches again turns every touch after a reset into a
    /// reload (loads would keep climbing), and one that matches too eagerly hands back the
    /// pre-reset instance. So it asserts both directions — reload once across the reset, then a
    /// fast-path hit on the rebuilt store with no further load.
    /// </summary>
    [Fact]
    public void AfterAPerTestReset_TheRebuiltStoreIsHydratedAgain()
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
            source, perTable, TableId, static () => new FakeStore());
        Assert.Equal(1, loads);

        perTable.Clear();                                   // ResetPerTestState()

        var second = RecordPatches.GetOrCreateHydratedDataAccessCore(
            source, perTable, TableId, static () => new FakeStore());

        Assert.NotSame(first, second);
        Assert.Equal(2, loads);
        Assert.Equal(new[] { BackupRow }, ((FakeStore)second).Rows.ToArray());

        // …and the rebuilt store is now the materialised one: a third touch is a fast-path hit,
        // same instance, no third load.
        var third = RecordPatches.GetOrCreateHydratedDataAccessCore(
            source, perTable, TableId, static () => new FakeStore());
        Assert.Same(second, third);
        Assert.Equal(2, loads);
    }

    /// <summary>
    /// The hand-out order must hold for a REPEAT touch too, not only for the racing pair: once
    /// hydration has finished, every later caller gets that same hydrated store back and no
    /// second load runs. Guards the lock-free fast path the fix adds.
    /// </summary>
    [Fact]
    public void AfterHydration_LaterTouchesGetTheSameStoreAndDoNotLoadAgain()
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
            source, perTable, TableId, static () => new FakeStore());
        var second = RecordPatches.GetOrCreateHydratedDataAccessCore(
            source, perTable, TableId, static () => new FakeStore());

        Assert.Same(first, second);
        Assert.Equal(1, loads);
        Assert.Equal(new[] { BackupRow }, ((FakeStore)second).Rows.ToArray());
    }

    /// <summary>
    /// A run without --test-data installs no loader, so there is no create -> hydrate window to
    /// protect and the storage is handed out as it always was: one instance per (source, table),
    /// created once, with nothing loaded into it.
    /// </summary>
    [Fact]
    public void WithNoLoaderInstalled_TheStoreIsCreatedOnceAndHandedBackUnchanged()
    {
        var source = new object();
        var perTable = new ConcurrentDictionary<int, object>();
        var creations = 0;
        Func<object> create = () => { Interlocked.Increment(ref creations); return new FakeStore(); };

        Assert.Null(RecordPatches.TestDataOnDemandLoader);
        var first = RecordPatches.GetOrCreateHydratedDataAccessCore(source, perTable, TableId, create);
        var second = RecordPatches.GetOrCreateHydratedDataAccessCore(source, perTable, TableId, create);

        Assert.Same(first, second);
        Assert.Equal(1, creations);
        Assert.Empty(((FakeStore)second).Rows);

        // A different DataAccessSource is a different store, exactly as perTable is keyed.
        var otherSource = new object();
        var otherPerTable = new ConcurrentDictionary<int, object>();
        var third = RecordPatches.GetOrCreateHydratedDataAccessCore(otherSource, otherPerTable, TableId, create);
        Assert.NotSame(first, third);
        Assert.Equal(2, creations);
    }

    /// <summary>
    /// The anti-deadlock rule, which is a property of the FIX and not of the defect: a thread
    /// that is already inside a materialisation must never block on another one.
    ///
    /// Hydrating a table runs BC's own metadata and NavValue construction, which can reach a
    /// Record of another table and land straight back in GetDataAccessForTableCore. If that
    /// nested call could wait on the other table's gate while the thread holding that gate
    /// reached back for this one, the two would deadlock. This stages exactly that shape:
    /// thread A parks mid-hydration holding table X, and thread B — itself mid-hydration of
    /// table Y — asks for X. B has to come back with the storage A published rather than wait.
    ///
    /// A fix that simply took a per-table lock everywhere hangs here, which is why this is a
    /// bounded wait: the failure mode being ruled out is a hang.
    /// </summary>
    [Fact]
    public void AThreadInsideAHydration_DoesNotBlockOnAnotherThreadsMaterialisation()
    {
        var source = new object();
        var perTable = new ConcurrentDictionary<int, object>();

        using var aIsHydrating = new ManualResetEventSlim(false);
        using var releaseA = new ManualResetEventSlim(false);
        object? nestedGot = null;

        RecordPatches.TestDataOnDemandLoader = (src, id) =>
        {
            if (id == TableId)
            {
                aIsHydrating.Set();
                releaseA.Wait(TimeSpan.FromSeconds(60));
                ((FakeStore)perTable[id]).Rows.Enqueue(BackupRow);
                return;
            }
            // Thread B, mid-hydration of OtherTableId, reaches a Record of TableId.
            nestedGot = RecordPatches.GetOrCreateHydratedDataAccessCore(
                src, perTable, TableId, static () => new FakeStore());
        };

        var a = Task.Run(() => RecordPatches.GetOrCreateHydratedDataAccessCore(
            source, perTable, TableId, static () => new FakeStore()));
        Assert.True(aIsHydrating.Wait(TimeSpan.FromSeconds(30)), "thread A never reached its hydration");

        var b = Task.Run(() => RecordPatches.GetOrCreateHydratedDataAccessCore(
            source, perTable, OtherTableId, static () => new FakeStore()));

        Assert.True(b.Wait(TimeSpan.FromSeconds(20)),
            "the nested materialisation blocked on the gate thread A holds — that is the deadlock");

        // It came back with the storage A published, not with a second private one.
        Assert.Same(perTable[TableId], nestedGot);

        releaseA.Set();
        Assert.True(a.Wait(TimeSpan.FromSeconds(30)), "thread A never finished");
        Assert.Equal(new[] { BackupRow }, ((FakeStore)perTable[TableId]).Rows.ToArray());
    }
}
