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
    private const int ScopeRolledBack = 1_900_000_406;
    private const int ScopeCommitted = 1_900_000_407;
    private const int ScopeAfterEarlierRestart = 1_900_000_408;
    private const int ScopeOnlyRestart = 1_900_000_409;
    private const int ScopeExplicitCommit = 1_900_000_410;
    private const int ScopeTwoTruncates = 1_900_000_413;
    private const int NestedBothRolledBack = 1_900_000_414;
    private const int NestedInnerRolledBack = 1_900_000_411;

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

    // ---- #5527: a guarded Codeunit.Run's own rollback (PushTransactionWorldScope / PopTransactionWorldScope(restore)) ----
    // The helper stands in for DataProvider_TruncateAsync's two calls on the counter: journal it, then restart it.
    private static void Truncate(int tableId, long? counterBefore)
    {
        RecordPatches.NoteIdentityRestart(tableId, counterBefore);
        Restore(tableId, null);
    }

    [SkippableFact]
    public void GuardedScope_RolledBack_PutsTheCounterBackWhereItWasBeforeTheTruncate()
    {
        Ready();
        Raise(ScopeRolledBack, 3);
        RecordPatches.PushTransactionWorldScope();
        Truncate(ScopeRolledBack, 3);
        Assert.Equal(1, Take(ScopeRolledBack));        // the inner Insert, after the restart

        RecordPatches.PopTransactionWorldScope(restore: true);

        Assert.Equal(4, Take(ScopeRolledBack));        // the caller's next Insert
    }

    [SkippableFact]
    public void GuardedScope_RolledBack_OfACounterThatHadNeverStarted_LeavesItUnstarted()
    {
        Ready();
        RecordPatches.PushTransactionWorldScope();
        Truncate(ScopeRolledBack + 100, null);
        Assert.Equal(1, Take(ScopeRolledBack + 100));

        RecordPatches.PopTransactionWorldScope(restore: true);

        Assert.Equal(1, Take(ScopeRolledBack + 100));
    }

    [SkippableFact]
    public void GuardedScope_Committed_ForgetsTheRestart_SoALaterTopLevelRollbackLeavesTheCounterAlone()
    {
        Ready();
        Raise(ScopeCommitted, 3);
        RecordPatches.PushTransactionWorldScope();
        Truncate(ScopeCommitted, 3);
        Assert.Equal(1, Take(ScopeCommitted));

        RecordPatches.PopTransactionWorldScope(restore: false);
        RecordPatches.RollbackToCommitPoint(null);     // the rows are durable, so the restart is too (as for the row snapshot)

        Assert.Equal(2, Take(ScopeCommitted));
    }

    [SkippableFact]
    public void GuardedScope_RolledBack_KeepsAnEarlierTruncateOfTheCaller_ForTheCallersOwnRollback()
    {
        Ready();
        Raise(ScopeAfterEarlierRestart, 5);
        Truncate(ScopeAfterEarlierRestart, 5);         // the caller's own Truncate(true), before the guarded run
        Assert.Equal(1, Take(ScopeAfterEarlierRestart));
        RecordPatches.PushTransactionWorldScope();
        Truncate(ScopeAfterEarlierRestart, 1);         // the guarded run's, with the counter now at 1
        Assert.Equal(1, Take(ScopeAfterEarlierRestart));

        RecordPatches.PopTransactionWorldScope(restore: true);

        Assert.Equal(2, Take(ScopeAfterEarlierRestart));   // back to where the scope found it
        RecordPatches.RollbackToCommitPoint(null);
        Assert.Equal(6, Take(ScopeAfterEarlierRestart));   // and the caller's rollback still undoes ITS Truncate
    }

    [SkippableFact]
    public void GuardedScope_RolledBack_ForgetsATruncateOnlyItMade_SoTheCallersRollbackLeavesLaterValuesAlone()
    {
        Ready();
        Raise(ScopeOnlyRestart, 3);
        RecordPatches.PushTransactionWorldScope();
        Truncate(ScopeOnlyRestart, 3);
        RecordPatches.PopTransactionWorldScope(restore: true);
        Assert.Equal(4, Take(ScopeOnlyRestart));       // taken by the caller after the guarded run

        RecordPatches.RollbackToCommitPoint(null);     // nobody truncated at the caller's level

        Assert.Equal(5, Take(ScopeOnlyRestart));
    }

    [SkippableFact]
    public void GuardedScope_AfterAnExplicitCommit_HasNothingLeftToRestore()
    {
        Ready();
        Raise(ScopeExplicitCommit, 3);
        RecordPatches.PushTransactionWorldScope();
        Truncate(ScopeExplicitCommit, 3);
        Assert.Equal(1, Take(ScopeExplicitCommit));
        RecordPatches.MarkExplicitCommitPoint();       // AL Commit() inside the guarded run

        RecordPatches.PopTransactionWorldScope(restore: true);

        Assert.Equal(2, Take(ScopeExplicitCommit));
    }

    [SkippableFact]
    public void GuardedScope_RolledBack_AfterTwoTruncates_PutsBackTheValueFromBeforeTheFirst()
    {
        Ready();
        Raise(ScopeTwoTruncates, 5);
        RecordPatches.PushTransactionWorldScope();
        Truncate(ScopeTwoTruncates, 5);
        Take(ScopeTwoTruncates);
        Take(ScopeTwoTruncates);
        Truncate(ScopeTwoTruncates, 2);                // a second Truncate(true), counter now 2
        Assert.Equal(1, Take(ScopeTwoTruncates));

        RecordPatches.PopTransactionWorldScope(restore: true);

        Assert.Equal(6, Take(ScopeTwoTruncates));
    }

    [SkippableFact]
    public void NestedGuardedScopes_InnerRolledBack_RestoresTheCounter_OuterStillHoldsItsOwnRestart()
    {
        Ready();
        Raise(NestedInnerRolledBack, 3);
        RecordPatches.PushTransactionWorldScope();     // outer
        RecordPatches.PushTransactionWorldScope();     // inner
        Truncate(NestedInnerRolledBack, 3);
        Assert.Equal(1, Take(NestedInnerRolledBack));

        RecordPatches.PopTransactionWorldScope(restore: true);   // inner fails
        Assert.Equal(4, Take(NestedInnerRolledBack));             // the outer's next Insert

        RecordPatches.PopTransactionWorldScope(restore: false);  // outer succeeds
        Assert.Equal(5, Take(NestedInnerRolledBack));
    }

    [SkippableFact]
    public void NestedGuardedScopes_InnerThenOuterRolledBack_TheOuterDoesNotRestoreATruncateTheInnerAlreadyUndid()
    {
        Ready();
        Raise(NestedBothRolledBack, 3);
        RecordPatches.PushTransactionWorldScope();     // outer
        RecordPatches.PushTransactionWorldScope();     // inner
        Truncate(NestedBothRolledBack, 3);
        RecordPatches.PopTransactionWorldScope(restore: true);   // inner fails: counter back at 3
        Assert.Equal(4, Take(NestedBothRolledBack));              // the outer takes a value, then fails too

        RecordPatches.PopTransactionWorldScope(restore: true);   // outer fails: it truncated nothing itself

        Assert.Equal(5, Take(NestedBothRolledBack));              // the value the outer took stays consumed
    }
}
