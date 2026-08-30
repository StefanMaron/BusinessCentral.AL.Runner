// CodeunitRunWriteTransactionRefusalTests — AlRunner#2133.
//
// This is a RUNNER-MECHANISM test, not a claim about what real BC does. The BC-observable
// claim ("a guarded Codeunit.Run — one whose Boolean result is consumed — is refused while
// the caller has an uncommitted write pending, while the statement form is allowed") is
// MEASURED upstream against a live BC service tier: codeunit/TestCodeunitRunWriteTransaction.al
// in StefanMaron/BusinessCentral.AL.Language.Tests, merged as commit 30d46f95 (corpus PR #75),
// all three of its assertions green on BC 27.5 and BC 28.3.
//
// What THIS test pins is our own wiring, which the corpus cannot see:
//
//   * The runner REPLACES NavCodeunit.DoRunAsync / NavCodeunit.RunCodeunit outright (see
//     CodeunitPatches.cs), so BC's own copy of the check — TransactionManager
//     .BeginTransactionWorld → ThrowIfWriteTransactionStarted, reached from
//     SessionTransactionExtensions.BeginTransactionWorldAndTransaction, which BC's
//     DoRunAsync calls on the `errorLevel != DataError.ThrowError` branch — never runs for
//     Codeunit.Run here. The refusal has to be re-issued by the replacement bodies.
//
//   * The refusal must sit OUTSIDE the `catch when (trap)` block, exactly as BC places
//     BeginTransactionWorldAndTransaction outside the try whose catch suppresses the
//     codeunit's own errors. A regression that moves it inside would silently turn the
//     refusal into `Codeunit.Run` returning false, which the corpus test would notice only
//     as a confusing "expected an error" failure.
//
//   * The AL-visible message must be BC's own resource, and the two resources go the
//     opposite way round from what their names suggest: the AL-visible Message is the
//     generic Lang.TransactionWorldWithActiveWriteTransactionError, and the text that
//     actually names Codeunit.Run is Lang.TransactionWorldWithActiveWriteTransaction,
//     carried as DetailedErrorMessage. This test compares against those resources by
//     reflection rather than against a literal, so it stays true across BC versions.

using System.Reflection;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Types;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class CodeunitRunWriteTransactionRefusalTests
{
    private readonly BcEngineFixture _engine;

    public CodeunitRunWriteTransactionRefusalTests(BcEngineFixture engine) => _engine = engine;

    private static string LangString(string name)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var lang = asm.GetType("Microsoft.Dynamics.Nav.Common.Language.Lang", throwOnError: false);
            var value = lang?.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                            ?.GetValue(null) as string;
            if (!string.IsNullOrEmpty(value)) return value;
        }
        throw new InvalidOperationException(
            $"Lang.{name} not found — Microsoft.Dynamics.Nav.Language.dll shape changed.");
    }

    [SkippableFact]
    public void ThrowIfWriteTransactionStarted_NoPendingWrite_DoesNotThrow()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        ALDatabasePatches.ResetWriteTransactionState();
        Assert.False(ALDatabasePatches.HasWriteTransaction(null),
            "precondition: write-transaction state must start clear");

        ALDatabasePatches.ThrowIfWriteTransactionStarted();
    }

    [SkippableFact]
    public void ThrowIfWriteTransactionStarted_PendingWrite_ThrowsBcsOwnMessageAndDetail()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        ALDatabasePatches.ResetWriteTransactionState();
        ALDatabasePatches.NoteRecordWrite(null);

        var ex = Assert.ThrowsAny<Exception>(() => ALDatabasePatches.ThrowIfWriteTransactionStarted());

        Assert.Equal("NavCSideException", ex.GetType().Name);
        Assert.Equal(LangString("TransactionWorldWithActiveWriteTransactionError"), ex.Message);

        var detail = ex.GetType()
            .GetProperty("DetailedErrorMessage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(ex) as string;
        Assert.Equal(LangString("TransactionWorldWithActiveWriteTransaction"), detail);

        // The detail is the half that names the rule; keep an explicit anchor on it so a
        // regression that drops DetailedErrorMessage cannot pass by leaving it null.
        Assert.Contains("Codeunit.Run is allowed in write transactions only if the return value is not used",
            detail);
    }

    [SkippableFact]
    public void ThrowIfWriteTransactionStarted_AfterCommit_DoesNotThrow()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        ALDatabasePatches.ResetWriteTransactionState();
        ALDatabasePatches.NoteRecordWrite(null);
        Assert.True(ALDatabasePatches.HasWriteTransaction(null),
            "precondition: the noted write must have opened the write transaction");

        ALDatabasePatches.ALDatabase_ALCommit();

        ALDatabasePatches.ThrowIfWriteTransactionStarted();
    }

    [SkippableFact]
    public void RunCodeunit_GuardedWithPendingWrite_ThrowsInsteadOfReturningFalse()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        ALDatabasePatches.ResetWriteTransactionState();
        ALDatabasePatches.NoteRecordWrite(null);

        // TrapError is the errorLevel AL's compiler emits when the Boolean result is consumed.
        // The refusal must escape the trap: `Ok := Codeunit.Run(...)` errors, it does not
        // quietly evaluate to false.
        var ex = Assert.ThrowsAny<Exception>(
            () => BcRuntime.NavCodeunit_RunCodeunit(DataError.TrapError, 60190, null));

        Assert.Equal("NavCSideException", ex.GetType().Name);
        Assert.Equal(LangString("TransactionWorldWithActiveWriteTransactionError"), ex.Message);
    }

    [SkippableFact]
    public void RunCodeunit_UnguardedWithPendingWrite_IsNotRefused()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        ALDatabasePatches.ResetWriteTransactionState();
        ALDatabasePatches.NoteRecordWrite(null);

        // ThrowError is the statement form. BC takes its plain BeginTransaction branch, which
        // has no write-transaction check at all, so the pending write must not stop the call.
        // Resolving codeunit 60190 fails here for an unrelated reason (no AL test assembly is
        // loaded in this fixture) — the point is only that the failure is NOT the refusal.
        var refusal = LangString("TransactionWorldWithActiveWriteTransactionError");
        try
        {
            BcRuntime.NavCodeunit_RunCodeunit(DataError.ThrowError, 60190, null);
        }
        catch (Exception ex)
        {
            Assert.NotEqual(refusal, ex.Message);
        }
    }
}
