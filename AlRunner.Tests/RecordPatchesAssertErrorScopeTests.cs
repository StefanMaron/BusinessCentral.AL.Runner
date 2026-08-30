// RecordPatchesAssertErrorScopeTests — pins the BeginAssertErrorScope/EndAssertErrorScope
// nesting behaviour AlRunner#2142's TestTriggerRollback fix depends on.
//
// RecordPatches.ForceDurableFailedInserts (called from MethodScopePatches
// .NavMethodScope_AssertErrorCore's catch handler) must only force durable an Insert()
// attempt made by the SAME statement asserterror is CURRENTLY wrapping — never one made by
// an earlier, already-returned statement (that one stays fully subject to the ordinary
// roll-back-to-last-commit-point rule TestAssertErrorRollback.al pins). That isolation is
// entirely the job of BeginAssertErrorScope (push aside + clear) / EndAssertErrorScope
// (restore), tested here directly with plain dummy objects — no BC skeleton needed, since
// the scoping itself doesn't touch any BC type.
//
// _pendingInsertsInScope / _pendingInsertsScopeStack are [ThreadStatic], so this test does
// not need RecordPatchesSerialCollection (unlike the AL-source-parser tests in this
// assembly) — a parallel xunit test on a different thread has its own copies.
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class RecordPatchesAssertErrorScopeTests
{
    [Fact]
    public void NestedScope_DoesNotLeakInnerStatementsInsertsIntoOuterScope()
    {
        // Outer "statement" notes an insert attempt (as if `asserterror OuterInsert()`).
        RecordPatches.BeginAssertErrorScope();
        RecordPatches.NoteInsertAttempt(new object());
        Assert.Equal(1, RecordPatches.PendingInsertsCountForTests);

        // A NESTED asserterror'd statement (as if the outer statement's body itself
        // contains another `asserterror InnerInsert()`) must start from a CLEAN slate —
        // it must not see the outer statement's own pending insert.
        RecordPatches.BeginAssertErrorScope();
        Assert.Equal(0, RecordPatches.PendingInsertsCountForTests);
        RecordPatches.NoteInsertAttempt(new object());
        RecordPatches.NoteInsertAttempt(new object());
        Assert.Equal(2, RecordPatches.PendingInsertsCountForTests);

        // Ending the inner scope must restore the OUTER's own pending list, discarding
        // whatever the inner statement accumulated — proving that AlRunner#2142's
        // ForceDurableFailedInserts, called only for the CURRENTLY ending scope, can never
        // reach into a different statement's insert attempts once nesting unwinds.
        RecordPatches.EndAssertErrorScope();
        Assert.Equal(1, RecordPatches.PendingInsertsCountForTests);

        RecordPatches.EndAssertErrorScope();
        Assert.Equal(0, RecordPatches.PendingInsertsCountForTests);
    }

    [Fact]
    public void SequentialScopes_EarlierStatementsInsertNeverReappearsInALaterScope()
    {
        // First statement's own asserterror scope: one insert attempt made, scope ends
        // normally (as if that Insert() succeeded — no exception, so
        // MethodScopePatches.NavMethodScope_AssertError's finally runs EndAssertErrorScope
        // without ForceDurableFailedInserts ever having consumed the list).
        RecordPatches.BeginAssertErrorScope();
        RecordPatches.NoteInsertAttempt(new object());
        Assert.Equal(1, RecordPatches.PendingInsertsCountForTests);
        RecordPatches.EndAssertErrorScope();

        // A LATER, textually unrelated asserterror'd statement (AlRunner#2142's own
        // TestScopeIsolationContracts.Test04 / TestTransactionContracts
        // .Error_After_Insert_Before_Commit_RecordPersists shape: an unrelated Error() after
        // an earlier, already-returned Insert()) must start with ZERO pending inserts — the
        // first statement's insert attempt must not leak forward into this new scope.
        RecordPatches.BeginAssertErrorScope();
        Assert.Equal(0, RecordPatches.PendingInsertsCountForTests);
        RecordPatches.EndAssertErrorScope();
    }

    [Fact]
    public void UnmatchedEnd_IsDefensiveNoOp_DoesNotThrow()
    {
        // EndAssertErrorScope's own doc calls this "defensive: unmatched End, nothing to
        // restore" — pin that it is actually a no-op, not a throw, since a future refactor
        // of NavMethodScope_AssertError's try/finally pairing could otherwise turn a benign
        // double-End into a test-run-ending exception instead of a wrong-but-survivable one.
        var before = RecordPatches.PendingInsertsCountForTests;
        var exception = Record.Exception(RecordPatches.EndAssertErrorScope);
        Assert.Null(exception);
        Assert.Equal(before, RecordPatches.PendingInsertsCountForTests);
    }
}
