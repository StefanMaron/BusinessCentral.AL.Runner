// AutoIncrementIdentityRestartTests -- the runner-side half of issue #5522.
//
// What BC does (after Truncate(true), a rollback to the last commit hands the AutoIncrement key out again) is a claim
// about BC and is proven upstream by corpus codeunit 67826 against a real service tier. None of it is asserted here.
// What is runner-specific: the journal that remembers a counter's value at its first restart since the commit point,
// and the two places that read it (the rollback, the commit point). Both are plain statics no AL test can reach in
// isolation, so a wiring drop shows up as a corpus test failing far downstream.
using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class AutoIncrementIdentityRestartTests
{
    private readonly BcEngineFixture _engine;

    public AutoIncrementIdentityRestartTests(BcEngineFixture engine) => _engine = engine;

    private static Type BcRuntimeType =>
        typeof(RecordPatches).Assembly.GetType("AlRunner.BcRuntime")
        ?? throw new InvalidOperationException("AlRunner.BcRuntime not found");

    private static MethodInfo Method(string name) =>
        BcRuntimeType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException($"BcRuntime.{name} not found -- Truncate(true) can no longer be undone by a rollback (#5522)");

    private static long Take(int tableId) => (long)Method("TakeAutoIncrementValue").Invoke(null, new object[] { tableId, 1 })!;

    private static void Raise(int tableId, long key) =>
        Method("RaiseAutoIncrementCounterTo").Invoke(null, new object[] { tableId, 1, key });

    private static void Restore(int tableId, long? value) =>
        Method("RestoreAutoIncrementCounter").Invoke(null, new object?[] { tableId, value });

    // Table ids far outside anything the runner materialises, one per test, so no test sees another's counter.
    private const int AfterUse = 1_900_000_401;
    private const int NeverUsed = 1_900_000_402;
    private const int FirstRestartWins = 1_900_000_403;
    private const int CommittedAway = 1_900_000_404;
    private const int Untouched = 1_900_000_405;

    private void Ready()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        RecordPatches.MarkCommitPoint();
    }

    [SkippableFact]
    public void Rollback_AfterARestartOfAUsedCounter_PutsTheCounterBackWhereItWas()
    {
        Ready();
        Raise(AfterUse, 3);
        RecordPatches.NoteIdentityRestart(AfterUse, 3);
        Restore(AfterUse, null);                       // what Truncate(true) does to the counter
        Assert.Equal(1, Take(AfterUse));               // handed out after the restart
        Assert.Equal(2, Take(AfterUse));

        RecordPatches.RollbackToCommitPoint(null);

        Assert.Equal(4, Take(AfterUse));
    }

    [SkippableFact]
    public void Rollback_AfterARestartOfACounterThatHadNeverStarted_LeavesItUnstarted()
    {
        Ready();
        RecordPatches.NoteIdentityRestart(NeverUsed, null);
        Assert.Equal(1, Take(NeverUsed));

        RecordPatches.RollbackToCommitPoint(null);

        Assert.Equal(1, Take(NeverUsed));
    }

    [SkippableFact]
    public void Rollback_KeepsTheValueFromBeforeTheFirstRestart_NotTheOneFromBeforeALaterRestart()
    {
        Ready();
        Raise(FirstRestartWins, 10);
        RecordPatches.NoteIdentityRestart(FirstRestartWins, 10);
        Restore(FirstRestartWins, null);
        Assert.Equal(1, Take(FirstRestartWins));
        RecordPatches.NoteIdentityRestart(FirstRestartWins, 1);     // a second Truncate(true), counter now 1
        Restore(FirstRestartWins, null);

        RecordPatches.RollbackToCommitPoint(null);

        Assert.Equal(11, Take(FirstRestartWins));
    }

    [SkippableFact]
    public void CommitPoint_ForgetsTheRestart_SoALaterRollbackLeavesTheCounterAlone()
    {
        Ready();
        Raise(CommittedAway, 3);
        RecordPatches.NoteIdentityRestart(CommittedAway, 3);
        Restore(CommittedAway, null);
        Assert.Equal(1, Take(CommittedAway));
        RecordPatches.MarkCommitPoint();                // Commit() after the Truncate

        RecordPatches.RollbackToCommitPoint(null);

        Assert.Equal(2, Take(CommittedAway));           // the value taken after the commit is consumed
    }

    [SkippableFact]
    public void Rollback_DoesNotTouchACounterNobodyRestarted()
    {
        Ready();
        Raise(Untouched, 7);
        RecordPatches.RollbackToCommitPoint(null);
        Assert.Equal(8, Take(Untouched));
    }
}
