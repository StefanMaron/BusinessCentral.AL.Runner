// XmlPortImportWriteTransactionTests — AlRunner#2184.
//
// A RUNNER-MECHANISM test. The BC-observable claim (a value-consuming XmlPort.Import is refused
// while the caller holds an uncommitted write; the static form answers false, the instance form
// raises) is measured upstream by corpus codeunit 60041 "Test XmlPort Import Write Tx".
//
// What this pins is the pair of helpers Cecil prepends onto BC's
// SessionTransactionExtensions.BeginTransactionWorldAndTransaction / EndTransactionWorldAndTransaction
// (NclCecilRewrite.Forms.cs block 8h): the refusal fires before the depth counter moves, and the
// world's end leaves the caller with no write transaction.

using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class XmlPortImportWriteTransactionTests
{
    private readonly BcEngineFixture _engine;

    public XmlPortImportWriteTransactionTests(BcEngineFixture engine) => _engine = engine;

    private static int RunTransactionDepth() =>
        (int)(typeof(ALDatabasePatches)
                  .GetField("_runTransactionDepth", BindingFlags.Static | BindingFlags.NonPublic)
                  ?.GetValue(null)
              ?? throw new InvalidOperationException("ALDatabasePatches._runTransactionDepth not found"));

    private static int OpenScopeCount()
    {
        var stack = typeof(RecordPatches)
                        .GetField("_txScopeStack", BindingFlags.Static | BindingFlags.NonPublic)
                        ?.GetValue(null)
                    ?? throw new InvalidOperationException("RecordPatches._txScopeStack not found");
        return (int)stack.GetType().GetProperty("Count")!.GetValue(stack)!;
    }

    private void Reset()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        ALDatabasePatches.ResetWriteTransactionState();
        ALDatabasePatches.ExitNoTransactionScope(); // zeroes the depth counter
        Assert.Equal(0, RunTransactionDepth());
    }

    [SkippableFact]
    public void BeginWorld_PendingWrite_IsRefused_AndLeavesDepthUnchanged()
    {
        Reset();
        ALDatabasePatches.NoteRecordWrite(null);
        var scopes = OpenScopeCount();

        var ex = Assert.ThrowsAny<Exception>(() => ALDatabasePatches.NoteBcBeginTransactionWorld());

        Assert.Equal("NavCSideException", ex.GetType().Name);
        Assert.Contains("the transaction is stopped", ex.Message);
        // BC calls Begin outside the try whose finally runs End, so a refused Begin must not
        // have counted a transaction that nothing will close.
        Assert.Equal(0, RunTransactionDepth());
        Assert.Equal(scopes, OpenScopeCount()); // nor pushed a scope nothing will pop (#4643)
        Assert.True(ALDatabasePatches.HasWriteTransaction(null),
            "a refusal must leave the caller's own pending write in place");
    }

    [SkippableFact]
    public void BeginWorld_NoPendingWrite_IsAllowed_AndCountsOneTransaction()
    {
        Reset();
        var scopes = OpenScopeCount();

        ALDatabasePatches.NoteBcBeginTransactionWorld();
        Assert.Equal(1, RunTransactionDepth());
        Assert.Equal(scopes + 1, OpenScopeCount()); // the world's own rollback scope (#4643)

        ALDatabasePatches.NoteBcEndTransactionWorld(null, true);
        Assert.Equal(0, RunTransactionDepth());
        Assert.Equal(scopes, OpenScopeCount());
    }

    [SkippableFact]
    public void EndWorld_WithoutCommit_PopsTheScopeAndLeavesNoWriteTransaction()
    {
        Reset();
        var scopes = OpenScopeCount();

        ALDatabasePatches.NoteBcBeginTransactionWorld();
        ALDatabasePatches.NoteRecordWrite(null);
        ALDatabasePatches.NoteBcEndTransactionWorld(null, false);

        Assert.Equal(0, RunTransactionDepth());
        Assert.Equal(scopes, OpenScopeCount());
        Assert.False(ALDatabasePatches.HasWriteTransaction(null),
            "a rolled-back world leaves the caller with no write transaction either");
    }

    [SkippableFact]
    public void EndWorld_AfterTheWorldWrote_LeavesTheCallerWithNoWriteTransaction()
    {
        Reset();

        ALDatabasePatches.NoteBcBeginTransactionWorld();
        ALDatabasePatches.NoteRecordWrite(null); // the import writing its rows
        Assert.True(ALDatabasePatches.HasWriteTransaction(null),
            "precondition: the write inside the world must be visible as a write transaction");

        ALDatabasePatches.NoteBcEndTransactionWorld(null, true);

        Assert.False(ALDatabasePatches.HasWriteTransaction(null),
            "the world owned the write; once it ends the caller holds none");
        // And so a second value-consuming import is allowed.
        ALDatabasePatches.NoteBcBeginTransactionWorld();
        ALDatabasePatches.NoteBcEndTransactionWorld(null, true);
    }

    [SkippableFact]
    public void PlainBeginTransaction_PendingWrite_IsNotRefused()
    {
        Reset();
        ALDatabasePatches.NoteRecordWrite(null);

        // The statement form (DataError.ThrowError) reaches BC's plain BeginTransaction, whose
        // prepend is the depth note alone: it joins the caller's transaction.
        ALDatabasePatches.NoteBcBeginTransaction();
        ALDatabasePatches.NoteBcEndTransaction();

        Assert.True(ALDatabasePatches.HasWriteTransaction(null),
            "a plain nested transaction must not end the caller's write transaction");
    }
}
