// TestDataSummarySnapshotTests — the proving test for issue #3025: the --test-data summary
// must be ONE snapshot, not several separately-taken reads glued together.
//
// WHAT IS UNDER TEST
//   TestDataProvisioner.TallyBoard.Capture() builds the Summary a person reads at the end of a
//   --test-data run. Before #3025 it took six independent reads of six independent counters,
//   and the counters were bumped one at a time. Two threads can be inside LoadOnDemand at once
//   — TestExecutor.InvokeWithTimeout abandons a test thread on watchdog expiry instead of
//   killing it (thread.Join(timeout)), so it keeps hydrating while the bundle loop carries on
//   in the same process — so a Capture() racing a hydration could report a COMBINATION that was
//   never true at any instant: rows counted from a table not yet counted as hydrated.
//
//   The extreme case is the readable one: "loaded 4200 row(s) in 0 table(s)". Each number is
//   individually correct and the sentence is false, which is the worst kind of diagnostic —
//   --test-data is mandatory for the Microsoft BaseApp buckets, where roughly 40% of failures
//   in one measured no-test-data run were missing setup data rather than defects, so this
//   summary is what a person uses to decide whether a run is trustworthy at all.
//
// WHY THIS IS NOT A BC-BEHAVIOUR TEST
//   Nothing here asserts anything about Business Central. It is about the internal consistency
//   of the runner's own reporting, so bc-behavior-tests-go-upstream.md does not send it to the
//   al-language corpus.
//
// WHY IT IS A HAMMER AND WHAT THAT COSTS
//   There is no seam that forces one specific interleaving, so this drives writers and a reader
//   concurrently over a board no other test can reach and asserts an invariant on EVERY snapshot
//   the reader takes. Against the pre-#3025 read strategy it fails within the first few hundred
//   snapshots, every run. Against the fixed one it cannot fail, because a snapshot is a single
//   reference read of an immutable record: there is no interleaving left to hit. The test is
//   therefore deterministic in the direction that matters (green is a proof, red is a
//   reproduction), and its RED was observed, not assumed.
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestDataSummarySnapshotTests
{
    // One "table" the writers hydrate. Three different per-table constants so a snapshot that
    // mixes an old table count with a new column count is caught on whichever field skewed,
    // and so no invariant below can be satisfied by two fields happening to be equal.
    private const int RowsPerTable = 100;
    private const int DroppedPerTable = 3;
    private const int NotInThisBuildPerTable = 5;

    private const string Backup = "/backups/CRONUS.bak";
    private const string Company = "CRONUS International Ltd_";
    private const int SkippedAmbiguous = 7;

    /// <summary>
    /// The central claim. While writers hydrate, every summary a reader takes describes a state
    /// that really existed: the rows, the dropped columns and the columns-not-in-this-build all
    /// belong to exactly the tables the same summary counts as hydrated.
    ///
    /// The writers keep running for the whole of the reader's loop, so every one of those
    /// snapshots is mid-flight by construction rather than by luck, and they each land one
    /// increment before the reader starts, so no snapshot is trivially the empty board.
    /// </summary>
    [Fact]
    public void ASummaryTakenWhileHydrationRuns_DescribesAStateThatExisted()
    {
        const int Writers = 3;
        const int Snapshots = 300_000;

        var board = new TestDataProvisioner.TallyBoard();
        var stop = 0;
        var written = new int[Writers];
        var primed = new CountdownEvent(Writers);

        var writers = Enumerable.Range(0, Writers).Select(w => new Thread(() =>
        {
            board.NoteHydrated(RowsPerTable, DroppedPerTable, NotInThisBuildPerTable);
            written[w] = 1;
            primed.Signal();
            while (Volatile.Read(ref stop) == 0)
            {
                board.NoteHydrated(RowsPerTable, DroppedPerTable, NotInThisBuildPerTable);
                written[w]++;
            }
        }) { IsBackground = true, Name = $"tally-writer-{w}" }).ToArray();

        foreach (var t in writers) t.Start();
        Assert.True(primed.Wait(TimeSpan.FromSeconds(30)), "a tally writer never started");

        try
        {
            for (var i = 0; i < Snapshots; i++)
            {
                var s = board.Capture(Backup, Company, SkippedAmbiguous);

                // The impossible combination named in #3025, called out on its own so the
                // failure message says what went wrong rather than only that arithmetic failed.
                Assert.False(s.TablesHydrated == 0 && s.RowsHydrated > 0,
                    $"summary {i} reports {s.RowsHydrated} row(s) in 0 table(s) — rows from a "
                    + "table the same summary does not count as hydrated (#3025).");

                Assert.Equal(s.TablesHydrated * RowsPerTable, s.RowsHydrated);
                Assert.Equal(s.TablesHydrated * DroppedPerTable, s.ColumnsFromUninstalledApps);
                Assert.Equal(s.TablesHydrated * NotInThisBuildPerTable, s.ColumnsNotInThisBuild);

                // Carried through unchanged from the armed plan, so they are the control: if
                // these ever skew the defect is somewhere else entirely.
                Assert.Equal(Backup, s.BackupPath);
                Assert.Equal(Company, s.Company);
                Assert.Equal(SkippedAmbiguous, s.TablesSkippedAmbiguous);
            }
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            foreach (var t in writers) Assert.True(t.Join(TimeSpan.FromSeconds(60)), "a tally writer never finished");
        }

        // Not vacuous: the board really counted, and counted every update. An implementation
        // whose NoteHydrated did nothing would satisfy every invariant above (0 == 0 * 100) and
        // fails here.
        var total = written.Sum();
        Assert.True(total >= Writers, $"the writers only managed {total} update(s)");

        var final = board.Capture(Backup, Company, SkippedAmbiguous);
        Assert.Equal(total, final.TablesHydrated);
        Assert.Equal(total * RowsPerTable, final.RowsHydrated);
        Assert.Equal(total * DroppedPerTable, final.ColumnsFromUninstalledApps);
        Assert.Equal(total * NotInThisBuildPerTable, final.ColumnsNotInThisBuild);
    }

    /// <summary>
    /// The other direction, single-threaded and exact: each writer counts the thing it is named
    /// for and nothing else, so the invariants above are pinned to concrete values rather than
    /// to a ratio that a uniformly-wrong implementation could also satisfy.
    ///
    /// The zero-row case is the one worth stating: a table the backup offers but which holds no
    /// rows is NOT a table hydrated, while the extension columns dropped for it still happened
    /// and are still counted. That asymmetry is what makes "rows &gt; 0 implies tables &gt;= 1"
    /// a real invariant rather than a coincidence.
    /// </summary>
    [Fact]
    public void EachTally_CountsItsOwnEventAndNoOther()
    {
        var board = new TestDataProvisioner.TallyBoard();

        var empty = board.Capture(Backup, Company, SkippedAmbiguous);
        Assert.Equal(0, empty.TablesHydrated);
        Assert.Equal(0, empty.RowsHydrated);
        Assert.Equal(0, empty.TablesRefused);
        Assert.Equal(0, empty.TablesRefusedByReader);
        Assert.Equal(0, empty.ColumnsFromUninstalledApps);
        Assert.Equal(0, empty.ColumnsNotInThisBuild);

        board.NoteHydrated(rows: 40, droppedColumns: 3, columnsNotInThisBuild: 5);
        board.NoteHydrated(rows: 0, droppedColumns: 2, columnsNotInThisBuild: 0);
        board.NoteRefused();
        board.NoteRefused();
        board.NoteReaderRefused();

        var s = board.Capture(Backup, Company, SkippedAmbiguous);
        Assert.Equal(1, s.TablesHydrated);          // the empty table is not one of them
        Assert.Equal(40, s.RowsHydrated);
        Assert.Equal(2, s.TablesRefused);
        Assert.Equal(1, s.TablesRefusedByReader);
        Assert.Equal(5, s.ColumnsFromUninstalledApps);   // 3 + 2, the empty table's included
        Assert.Equal(5, s.ColumnsNotInThisBuild);
        Assert.Equal(SkippedAmbiguous, s.TablesSkippedAmbiguous);

        // And the sentence the user actually reads carries those same numbers.
        Assert.Contains("loaded 40 row(s) in 1 table(s)", s.Describe());
        Assert.Contains("2 refused (unsupported value types or unknown columns)", s.Describe());
        Assert.Contains("1 refused by the backup reader", s.Describe());
    }
}
