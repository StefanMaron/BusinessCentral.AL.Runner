// InstallBaselineAppendConcurrencyTests — the proving tests for issue #2914.
//
// WHAT IS UNDER TEST
//   RecordPatches.AppendBaselineTable is the only writer of the install baselines outside the
//   capture path (#2262's lazy --test-data load). It walks and appends to plain List<T>s that
//   other references are already holding — the per-app-group singleton AND TestExecutor's
//   process-lifetime dep+company cache both hand out the same list objects — and it did so with
//   no synchronisation at all.
//
// WHY TWO THREADS CAN BE IN THERE AT ONCE
//   The callers run inside a --test-data hydration, which GetOrCreateHydratedDataAccessCore
//   serialises per (DataAccessSource, table id) — NOT globally, deliberately, because a
//   hydration must be able to nest (the wait-graph note in
//   RecordPatches.TableMaterialisation.cs). Two threads hydrating two different tables
//   therefore hold two different monitors and append to the same lists at the same time.
//
//   The second thread is real, not hypothetical: TestExecutor.InvokeWithTimeout runs every
//   [Test] on its own worker thread and only Join()s it for the timeout, so a test that
//   overruns the watchdog leaves its thread running — "the hung thread is never killed and
//   keeps mutating shared BC state" (TestExecutor.RunOne, Program.cs at the --exclude-test and
//   auto-resume comments). The bundle loop then carries on in the SAME process, and under
//   --server the next request does too, so the abandoned thread's lazy loads race the live
//   run's.
//
// WHY THE INTERLEAVING IS FORCED, NOT HOPED FOR
//   AppendInto's critical section calls nothing a test could stand in for, so it carries a
//   test-only probe (RecordPatches.AppendInterleaveProbeForTests, null on every real run) at
//   the two points where a second thread's interleaving was observable. One thread parks there;
//   the other is only started once it is parked. On the unsynchronised code the second thread
//   runs the whole append while the first is parked, and the assertions below are on the
//   resulting STATE, never on timing. On the fixed code the second thread cannot get in at all,
//   so the release never comes and the parked thread's bounded wait expires instead — the
//   expiry is what lets the test finish, never the thing being asserted.
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

/// <summary>Serialises every test that writes the install-baseline statics
/// (_installBaseline, _activeDepCompanyBaseline, AppendInterleaveProbeForTests). They are
/// process-global, so two such classes running in parallel would corrupt each other.</summary>
[CollectionDefinition(InstallBaselineStaticsCollection.Name, DisableParallelization = true)]
public sealed class InstallBaselineStaticsCollection
{
    public const string Name = "install-baseline-statics";
}

[Collection(InstallBaselineStaticsCollection.Name)]
public sealed class InstallBaselineAppendConcurrencyTests : IDisposable
{
    private const int TableA = 61030;
    private const int TableB = 61031;

    /// <summary>How long a parked thread waits to be released. On the unsynchronised code the
    /// release always arrives (that is the defect); on the fixed code it never can, so this is
    /// the whole cost of each test.</summary>
    private static readonly TimeSpan ParkTimeout = TimeSpan.FromSeconds(2);

    private readonly List<RecordPatches.BaselineSource>? _savedInstallBaseline;

    public InstallBaselineAppendConcurrencyTests()
    {
        // These tests hand AppendBaselineTable a synthetic DataAccessSource; leaving one in the
        // live baseline would hand _mCreateTempDataAccess an object that is not a
        // DataAccessSource the next time anything restores.
        _savedInstallBaseline = RecordPatches.InstallBaselineForTests;
        RecordPatches.InstallBaselineForTests = null;
        RecordPatches.SetActiveDepCompanyBaseline(null);
        RecordPatches.AppendInterleaveProbeForTests = null;
    }

    public void Dispose()
    {
        RecordPatches.AppendInterleaveProbeForTests = null;
        RecordPatches.InstallBaselineForTests = _savedInstallBaseline;
        RecordPatches.SetActiveDepCompanyBaseline(null);
    }

    private static RecordPatches.InstallBaselineSnapshot EmptySnapshot()
        => new(new List<RecordPatches.BaselineSource>(), null, null, null);

    private static NavValue[][] Rows(params string[] values)
        => values.Select(v => new NavValue[] { new NavText(0, v) }).ToArray();

    /// <summary>
    /// THE claim of #2914, first half. Two threads hydrating two DIFFERENT tables of the SAME
    /// DataAccessSource append concurrently. Whichever gets there first must be the only one to
    /// publish a BaselineSource for that source, because every later lookup — here and in
    /// RestoreInstallBaselineSnapshot — resolves it by ReferenceEquals on Source and stops at
    /// the first match. A second BaselineSource for the same source makes its tables
    /// unreachable: they are dropped at the next codeunit/test boundary restore and, because
    /// storage presence IS the "have we loaded this" answer, never reloaded either.
    ///
    /// Against the unsynchronised code both threads see no source, both publish one, and the
    /// loser's table is lost.
    /// </summary>
    [Fact]
    public void TwoThreadsAppendingDifferentTables_ProduceOneSourceCarryingBoth()
    {
        var source = new object();
        var depCompany = EmptySnapshot();
        RecordPatches.SetActiveDepCompanyBaseline(depCompany);

        using var firstIsParked = new ManualResetEventSlim(false);
        using var secondHasAppended = new ManualResetEventSlim(false);

        RecordPatches.AppendInterleaveProbeForTests = phase =>
        {
            if (phase != RecordPatches.AppendPhaseBeforeSourceAdd) return;
            // Only the FIRST thread parks; the second must run to completion, which is exactly
            // the interleaving that must be impossible.
            if (firstIsParked.IsSet) return;
            firstIsParked.Set();
            secondHasAppended.Wait(ParkTimeout);
        };

        var first = Task.Run(() =>
            RecordPatches.AppendBaselineTable(source, TableA, new object(), Rows("A-ROW")));

        Assert.True(firstIsParked.Wait(TimeSpan.FromSeconds(30)),
            "the first thread never reached the probe, so no interleaving was staged");

        var second = Task.Run(() =>
        {
            RecordPatches.AppendBaselineTable(source, TableB, new object(), Rows("B-ROW"));
            secondHasAppended.Set();
        });

        Assert.True(Task.WaitAll(new[] { first, second }, TimeSpan.FromSeconds(60)),
            "an append never completed");

        // One source for the one DataAccessSource…
        var only = Assert.Single(depCompany.Sources);
        Assert.Same(source, only.Source);

        // …carrying BOTH tables, with the rows each thread actually appended. A fix that
        // serialised the appends but dropped one of them would pass the count above.
        Assert.Equal(new[] { TableA, TableB }, only.Tables.Select(t => t.TableId).OrderBy(id => id).ToArray());
        Assert.Equal("A-ROW", only.Tables.Single(t => t.TableId == TableA).Rows[0][0].ToString());
        Assert.Equal("B-ROW", only.Tables.Single(t => t.TableId == TableB).Rows[0][0].ToString());
    }

    /// <summary>
    /// Second half. The idempotence guard ("a table this baseline already carries is left
    /// alone") is a check-then-act: two threads can both pass it for the same table id and both
    /// append, and the boundary restore then inserts the install-seeded rows twice — a
    /// duplicate-key error on the second insert, or silently doubled rows.
    ///
    /// The source already exists here, so the interleaving happens at the table-add phase.
    /// </summary>
    [Fact]
    public void TwoThreadsAppendingTheSameTable_PublishItExactlyOnce()
    {
        var source = new object();
        var depCompany = new RecordPatches.InstallBaselineSnapshot(
            new List<RecordPatches.BaselineSource>
            {
                new(source, new List<RecordPatches.BaselineTable>()),
            },
            null, null, null);
        RecordPatches.SetActiveDepCompanyBaseline(depCompany);

        using var firstIsParked = new ManualResetEventSlim(false);
        using var secondHasAppended = new ManualResetEventSlim(false);

        RecordPatches.AppendInterleaveProbeForTests = phase =>
        {
            if (phase != RecordPatches.AppendPhaseBeforeTableAdd) return;
            if (firstIsParked.IsSet) return;
            firstIsParked.Set();
            secondHasAppended.Wait(ParkTimeout);
        };

        var first = Task.Run(() =>
            RecordPatches.AppendBaselineTable(source, TableA, new object(), Rows("FIRST")));

        Assert.True(firstIsParked.Wait(TimeSpan.FromSeconds(30)),
            "the first thread never reached the probe, so no interleaving was staged");

        var second = Task.Run(() =>
        {
            RecordPatches.AppendBaselineTable(source, TableA, new object(), Rows("SECOND"));
            secondHasAppended.Set();
        });

        Assert.True(Task.WaitAll(new[] { first, second }, TimeSpan.FromSeconds(60)),
            "an append never completed");

        var only = Assert.Single(depCompany.Sources);
        var table = Assert.Single(only.Tables);           // NOT twice
        Assert.Equal(TableA, table.TableId);
        // Whichever thread won, it published one row set, not two concatenated ones.
        Assert.Single(table.Rows);
        Assert.Contains(table.Rows[0][0].ToString(), new[] { "FIRST", "SECOND" });
    }

    /// <summary>
    /// The identity semantics the lookup rests on, unchanged by the fix: BaselineSources are
    /// resolved by ReferenceEquals on Source, never by value equality. Two distinct
    /// DataAccessSource objects appending concurrently must therefore get a BaselineSource each
    /// — a "fix" that keyed a dictionary on the source by value equality (or merged the two
    /// because both are bare `object`s that compare equal under some comparer) would collapse
    /// them and put one store's rows in the other's baseline.
    /// </summary>
    [Fact]
    public void TwoThreadsAppendingForDifferentSources_KeepASourceEach()
    {
        var first = new object();
        var second = new object();
        var depCompany = EmptySnapshot();
        RecordPatches.SetActiveDepCompanyBaseline(depCompany);

        using var firstIsParked = new ManualResetEventSlim(false);
        using var secondHasAppended = new ManualResetEventSlim(false);

        RecordPatches.AppendInterleaveProbeForTests = phase =>
        {
            if (phase != RecordPatches.AppendPhaseBeforeSourceAdd) return;
            if (firstIsParked.IsSet) return;
            firstIsParked.Set();
            secondHasAppended.Wait(ParkTimeout);
        };

        var a = Task.Run(() =>
            RecordPatches.AppendBaselineTable(first, TableA, new object(), Rows("FIRST-SOURCE")));

        Assert.True(firstIsParked.Wait(TimeSpan.FromSeconds(30)),
            "the first thread never reached the probe, so no interleaving was staged");

        var b = Task.Run(() =>
        {
            RecordPatches.AppendBaselineTable(second, TableA, new object(), Rows("SECOND-SOURCE"));
            secondHasAppended.Set();
        });

        Assert.True(Task.WaitAll(new[] { a, b }, TimeSpan.FromSeconds(60)), "an append never completed");

        Assert.Equal(2, depCompany.Sources.Count);
        Assert.Equal("FIRST-SOURCE",
            depCompany.Sources.Single(s => ReferenceEquals(s.Source, first)).Tables[0].Rows[0][0].ToString());
        Assert.Equal("SECOND-SOURCE",
            depCompany.Sources.Single(s => ReferenceEquals(s.Source, second)).Tables[0].Rows[0][0].ToString());
    }

    /// <summary>
    /// The reader half of the same shape. RestoreInstallBaselineSnapshot walks the very lists
    /// AppendBaselineTable appends to — at every codeunit/test boundary, which is precisely when
    /// a lazily loading thread appends — and it walks them while calling into BC for each table
    /// (create the temp data access, insert every row), so the window is wide, not narrow. A
    /// plain List&lt;T&gt; enumerator throws InvalidOperationException ("Collection was
    /// modified") the moment an append lands inside it.
    ///
    /// So the restore walks a structurally stable copy taken under the same lock
    /// (StableBaselineSourcesForWalk), and this drives exactly the interleaving that used to
    /// tear it: an append fires from INSIDE the walk. Against a walk over the live lists both
    /// assertions below are unreachable — the enumerator throws first.
    ///
    /// The BC calls the restore makes with each table are not reproduced here; what is under
    /// test is the stability of the structure it is walking, which is what the append can break.
    /// </summary>
    [Fact]
    public void AnAppendFiringInsideTheRestoresWalk_DoesNotTearIt()
    {
        var source = new object();
        var sources = new List<RecordPatches.BaselineSource>
        {
            new(source, new List<RecordPatches.BaselineTable>
            {
                new(TableA, new object(), Rows("A-ROW")),
            }),
        };
        RecordPatches.SetActiveDepCompanyBaseline(
            new RecordPatches.InstallBaselineSnapshot(sources, null, null, null));

        var walkedTables = new List<int>();
        var appends = 0;

        // The walk RestoreInstallBaselineSnapshot does, over what it actually walks.
        foreach (var walkedSource in RecordPatches.StableBaselineSourcesForWalk(sources))
        {
            Assert.Same(source, walkedSource.Source);
            foreach (var table in walkedSource.Tables)
            {
                walkedTables.Add(table.TableId);
                if (appends++ > 0) continue;
                // A lazy --test-data load on another thread, landing mid-walk. Both lists this
                // append touches — the sources list and this source's Tables list — are the
                // ones being enumerated right now.
                var other = new object();
                var append = Task.Run(() =>
                {
                    RecordPatches.AppendBaselineTable(source, TableB, new object(), Rows("B-ROW"));
                    RecordPatches.AppendBaselineTable(other, TableA, new object(), Rows("OTHER"));
                });
                Assert.True(append.Wait(TimeSpan.FromSeconds(30)), "the append never completed");
            }
        }

        // The walk saw the structure as it was when it started, and completed.
        Assert.Equal(new[] { TableA }, walkedTables.ToArray());

        // …and nothing was dropped: both appends are in the live lists, for the NEXT walk.
        Assert.Equal(2, sources.Count);
        Assert.Equal(new[] { TableA, TableB },
            sources.Single(s => ReferenceEquals(s.Source, source)).Tables.Select(t => t.TableId).ToArray());
    }
}
