// SystemIdRestoreSuppressionTests — a snapshot replay is not an AL Insert (issue #2694).
//
// #2639 made TempTableDataProvider.Insert refuse a duplicate SystemId, modelling the clustered
// unique constraint real SQL Server puts on $systemId. That is right for an AL `Insert()`
// statement. It is a category error for the runner's OWN transaction rollback, which restores a
// table by clearing it and re-inserting the snapshot rows through that same provider method.
// Real BC's rollback is a transaction abort; it never issues an INSERT, so there is no
// constraint for a replay to violate.
//
// The consequence was total. Measured on Microsoft's Tests-SINGLESERVER (BC 28.1), traced to
// RecordPatches.RollbackToCommitPoint -> InsertRows -> the guard:
//
//     EXEC-FAIL: There is already a record in table User Setup that has the same values in a
//     unique index for the following fields: SystemId=4ca8d6ba-...
//
// 0 of 878 tests ran, on `main`, with the corpus green throughout. Bisected to #2639's commit:
// its parent d9f01ca1 runs all 878.
//
// A restore that throws half way is worse than either outcome it chooses between: the table is
// left holding some of the snapshot and none of the rest.
//
// NOTE the underlying data problem this exposed is NOT fixed here and is tracked separately:
// the snapshot legitimately contained two User Setup rows carrying the same SystemId, so
// something seeded them that way. Suppressing the check during a replay restores the run; it
// does not make those rows correct.

using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class SystemIdRestoreSuppressionTests
{
    [Fact]
    public void NotSuppressed_ByDefault()
        => Assert.False(RowVersionPatches.IsSystemIdUniquenessSuppressed);

    [Fact]
    public void Suppressed_InsideTheScope()
    {
        using (RowVersionPatches.SuppressSystemIdUniqueness())
            Assert.True(RowVersionPatches.IsSystemIdUniquenessSuppressed);
    }

    /// <summary>Restored on the way out, including when the restore throws — a leaked
    /// suppression would disable a real AL-visible integrity check for the rest of the process,
    /// turning #2639's fix into a no-op nobody would notice.</summary>
    [Fact]
    public void Restored_AfterTheScope_EvenOnAnException()
    {
        try
        {
            using (RowVersionPatches.SuppressSystemIdUniqueness())
                throw new InvalidOperationException("restore blew up");
        }
        catch (InvalidOperationException) { }

        Assert.False(RowVersionPatches.IsSystemIdUniquenessSuppressed);
    }

    /// <summary>Nesting restores to the enclosing state, not unconditionally to false: a
    /// rollback inside a rollback must not re-arm the check for the outer replay's remaining
    /// rows.</summary>
    [Fact]
    public void Nesting_RestoresTheEnclosingState()
    {
        using (RowVersionPatches.SuppressSystemIdUniqueness())
        {
            using (RowVersionPatches.SuppressSystemIdUniqueness())
                Assert.True(RowVersionPatches.IsSystemIdUniquenessSuppressed);

            Assert.True(RowVersionPatches.IsSystemIdUniquenessSuppressed);
        }
        Assert.False(RowVersionPatches.IsSystemIdUniquenessSuppressed);
    }

    /// <summary>The suppression is per-thread. AL test bodies run on their own thread (see
    /// TestExecutor.InvokeWithTimeout), and a process-wide flag would let one thread's restore
    /// silently disable the check for another's genuine AL Insert.</summary>
    [Fact]
    public void Suppression_DoesNotLeakToAnotherThread()
    {
        bool? seenOnOtherThread = null;
        using (RowVersionPatches.SuppressSystemIdUniqueness())
        {
            var t = new Thread(() => seenOnOtherThread = RowVersionPatches.IsSystemIdUniquenessSuppressed);
            t.Start();
            t.Join();
        }
        Assert.False(seenOnOtherThread);
    }
}
