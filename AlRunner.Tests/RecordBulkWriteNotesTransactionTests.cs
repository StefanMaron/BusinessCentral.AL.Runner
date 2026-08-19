// RecordBulkWriteNotesTransactionTests — AlRunner#1791 (Database.IsInWriteTransaction()
// read false at test entry after a zero-match DeleteAll()).
//
// This is a RUNNER-MECHANISM test, not a claim about what real BC does. The BC-observable
// claim ("DeleteAll()/ModifyAll() open a write transaction even when zero rows match, and
// Commit() clears it") is proven upstream against a live BC service tier — see
// StefanMaron/BusinessCentral.AL.Language.Tests PR #43 ("test(database): DeleteAll opens a
// write transaction even with zero matches"), merged and green on BC 27.5 + 28.3, adding
// TestBCDatabaseContracts.al's Database_IsInWriteTransaction_AfterDeleteAllWithNoMatches_*
// pair — already covered by the pinned submodule commit, no pin bump needed for this PR.
//
// What THIS test pins is OUR OWN wiring bug and its fix: decompiling the shipped
// Microsoft.Dynamics.Nav.Ncl.dll shows AL's compiler binds `Record.DeleteAll(RunTrigger)` /
// `Record.ModifyAll(...)` to the SYNC entry points `NavRecord.ALDeleteAll(bool)` /
// `ALModifyAll(int, NavValue, bool)`, which call the PROTECTED `DeleteAllAsync(bool)` /
// `ModifyAllAsync(NCLMetaField, NavValue, bool)` DIRECTLY — bypassing the "AL"-prefixed
// async siblings (`ALDeleteAllAsync` / `ALModifyAllAsync`) entirely. Before this fix,
// NclCecilRewrite's write-entry-point prepend list named the "AL"-prefixed async siblings,
// so it silently never fired for any DeleteAll()/ModifyAll() statement the AL compiler
// actually emits — verified by decompiling this project's OWN compiled test output (zero
// `.ALDeleteAllAsync(`/`.ALModifyAllAsync(` call sites anywhere in the corpus; 125
// `.ALDeleteAll(` call sites). The fix re-targets the prepend at `DeleteAllAsync` /
// `ModifyAllAsync` (the single, non-overloaded, protected `virtual` methods every entry
// surface funnels through exactly once), so a regression that reverts the prepend back to
// the "AL"-prefixed names fails THIS test in milliseconds, without needing the AL compiler
// or the corpus loaded a second time.
//
// Calling the protected methods directly (via reflection) on a bare, uninitialized NavRecord
// is deliberate and safe here: the prepend is the FIRST instruction in the method body, so it
// always runs before the original body (which would NRE on the record's unset Session/
// MetaTable) ever executes — exactly mirroring how the prepend is unconditional in the real
// pipeline (ALDatabasePatches.NoteRecordWrite excludes only IsTemporary records, which is
// covered by the negative case below).

using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class RecordBulkWriteNotesTransactionTests
{
    private readonly BcEngineFixture _engine;

    public RecordBulkWriteNotesTransactionTests(BcEngineFixture engine) => _engine = engine;

    private static FieldInfo ResolveIsTemporaryField()
        => typeof(NavRecord).GetField("isTemporary", BindingFlags.NonPublic | BindingFlags.Instance)
           ?? throw new InvalidOperationException("NavRecord.isTemporary not found — Ncl shape changed.");

    private static NavRecord NewBareRecord(bool temporary)
    {
        var record = (NavRecord)RuntimeHelpers.GetUninitializedObject(typeof(NavRecord));
        ResolveIsTemporaryField().SetValue(record, temporary);
        return record;
    }

    /// <summary>
    /// Invokes NavRecord's protected DeleteAllAsync(bool)/ModifyAllAsync(NCLMetaField,
    /// NavValue, bool) directly — the shared core every AL-facing entry surface (sync
    /// ALDeleteAll/ALModifyAll AND async ALDeleteAllAsync/ALModifyAllAsync) funnels
    /// through — and swallows whatever the un-initialized record's real body throws past
    /// the prepend (ThrowIfRecordStaleOrNotOpen etc.), since only the prepend's effect on
    /// ALDatabasePatches state is under test here.
    /// </summary>
    private static void InvokeIgnoringDownstreamFailure(NavRecord record, string methodName, object[] args)
    {
        var method = typeof(NavRecord).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"NavRecord.{methodName} not found — Ncl shape changed.");
        try
        {
            var result = method.Invoke(record, args);
            if (result is ValueTask vt) vt.AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // Expected: the record has no Session/MetaTable, so the original body (which
            // runs AFTER our prepend) fails downstream. Only the prepend's side effect is
            // under test.
        }
    }

    [SkippableFact]
    public void DeleteAllAsync_NonTemporaryRecord_NotesWriteBeforeOriginalBodyRuns()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        ALDatabasePatches.ResetWriteTransactionState();
        Assert.False(ALDatabasePatches.HasWriteTransaction(null),
            "precondition: write-transaction state must start clear");

        var record = NewBareRecord(temporary: false);
        InvokeIgnoringDownstreamFailure(record, "DeleteAllAsync", new object[] { false });

        Assert.True(ALDatabasePatches.HasWriteTransaction(null),
            "DeleteAllAsync must note a write before its original body runs — real BC issues " +
            "the DELETE statement (and so opens a write transaction) regardless of how many " +
            "rows end up matching");
    }

    [SkippableFact]
    public void ModifyAllAsync_NonTemporaryRecord_NotesWriteBeforeOriginalBodyRuns()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        ALDatabasePatches.ResetWriteTransactionState();
        Assert.False(ALDatabasePatches.HasWriteTransaction(null),
            "precondition: write-transaction state must start clear");

        var record = NewBareRecord(temporary: false);
        InvokeIgnoringDownstreamFailure(record, "ModifyAllAsync", new object?[] { null, null, false });

        Assert.True(ALDatabasePatches.HasWriteTransaction(null),
            "ModifyAllAsync must note a write before its original body runs, for the same " +
            "reason as DeleteAllAsync above");
    }

    [SkippableFact]
    public void DeleteAllAsync_TemporaryRecord_DoesNotNoteWrite()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        ALDatabasePatches.ResetWriteTransactionState();

        var record = NewBareRecord(temporary: true);
        InvokeIgnoringDownstreamFailure(record, "DeleteAllAsync", new object[] { false });

        Assert.False(ALDatabasePatches.HasWriteTransaction(null),
            "a temporary record touches no database, so DeleteAllAsync on one must not open " +
            "a write transaction — negative pairing for the two positive cases above");
    }
}
