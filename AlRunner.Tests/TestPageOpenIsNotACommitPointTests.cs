// TestPageOpenIsNotACommitPointTests — the rot guard for #2400.
//
// RunnerTestPageState.MarkOpened used to call RecordPatches.MarkCommitPoint() on every
// TestPage open, on the reading that "opening a page enters a new TRANSACTION WORLD, and
// TransactionManager.BeginTransactionWorld commits the active transaction on entering one".
// MarkCommitPoint() DISCARDS the snapshot a rollback restores from, so a [Test] that wrote
// rows and then opened a TestPage had nothing left to roll back to: its writes outlived the
// test, past [TransactionModel(TransactionModel::AutoRollback)] and past a later asserterror.
// In Microsoft's Tests-SINGLESERVER codeunit 134614 that is one test's InitializeData()
// leaving its "Security Group" rows for the next test to fail on with "The group SG1 already
// exists" — 13 of 15 tests in that codeunit, all cascading from the one root.
//
// The reading was half right, and the half that matters is wrong. A page CAN reach a commit:
// SessionTransactionExtensions.BeginTransactionWorld commits when the transaction-world count
// is zero. But in BC 28.1's Ncl it has exactly two callers, NavForm.RunModalAsync and
// NavReport.RunReportCoreAsync — Page.RunModal and Report.Run enter a transaction world, and
// a TestPage does not. NavTestPage.Open is base.Open(mode), then
// TestClientProxy<ITestPage>.Proxy(session.TestExecution.ClientSession.CreatePage(...)), then
// Attach(value). No transaction world anywhere on that path.
//
// These are deliberately RUNNER-INTERNAL and BC-SHAPE claims: that the runner's own
// MarkOpened no longer discards the commit point, and that the BC shape the removal rests on
// still holds. Whether a write made before a TestPage opens is still rolled back at the end
// of the test is a plain BC-behaviour claim and lives upstream, where a real service tier
// adjudicates it — corpus codeunit 60900 "Test TxModel Page Open"
// (StefanMaron/BusinessCentral.AL.Language.Tests), which writes a row, opens and closes a
// TestPage, and asserts the write is undone both by AutoRollback and by a later asserterror.
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Dynamics.Nav.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

// Reads the Ncl image this process actually loaded, which BcEngineBootstrap has already
// Cecil-rewritten in place — so it must share the serial bc-engine collection.
[Collection(BcEngineCollection.Name)]
public sealed class TestPageOpenIsNotACommitPointTests
{
    private readonly BcEngineFixture _engine;

    public TestPageOpenIsNotACommitPointTests(BcEngineFixture engine) => _engine = engine;

    private const string BeginTransactionWorld = "BeginTransactionWorld";
    private const string BeginTransactionWorldAndTransaction = "BeginTransactionWorldAndTransaction";

    /// <summary>
    /// Every method body declared by <paramref name="typeFullName"/> or by any type nested
    /// inside it — the compiler-generated async state machines live in nested types, and
    /// NavForm.RunModalAsync's call to BeginTransactionWorld is inside one of them, so a scan
    /// of the outer type alone would report "does not call it" for the one case that does.
    /// </summary>
    private static IEnumerable<MethodDefinition> BodiesOf(ModuleDefinition module, string typeFullName)
    {
        var root = module.GetType(typeFullName);
        Assert.True(root != null,
            $"{typeFullName} not found in the loaded Ncl image — Ncl shape changed; do not commit.");

        var stack = new Stack<TypeDefinition>();
        stack.Push(root!);
        while (stack.Count > 0)
        {
            var t = stack.Pop();
            foreach (var nested in t.NestedTypes) stack.Push(nested);
            foreach (var m in t.Methods)
                if (m.HasBody) yield return m;
        }
    }

    private static List<string> CallSitesOf(
        ModuleDefinition module, string typeFullName, params string[] calleeNames)
        => BodiesOf(module, typeFullName)
            .Where(m => m.Body.Instructions.Any(i =>
                (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                && i.Operand is MethodReference mr
                && calleeNames.Contains(mr.Name, StringComparer.Ordinal)))
            .Select(m => m.DeclaringType.FullName + "::" + m.Name)
            .ToList();

    private static ModuleDefinition Ncl()
        => AssemblyDefinition.ReadAssembly(typeof(ITreeObject).Assembly.Location).MainModule;

    /// <summary>
    /// Positive control: the transaction world a page CAN enter is real, and it is the MODAL
    /// path. Without this, the negative below would pass just as happily against a Cecil scan
    /// that had stopped finding anything at all — a rename of BeginTransactionWorld would turn
    /// the guard into a test that cannot fail.
    /// </summary>
    [SkippableFact]
    public void NavForm_ModalRun_DoesEnterATransactionWorld()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var sites = CallSitesOf(Ncl(), "Microsoft.Dynamics.Nav.Runtime.NavForm",
            BeginTransactionWorld, BeginTransactionWorldAndTransaction);

        Assert.True(sites.Count > 0,
            "NavForm (or one of its nested async state machines) must still call "
            + $"{BeginTransactionWorld} — that is the modal-page commit this guard's negative "
            + "half is defined against. If BC moved it, re-derive where a page commits before "
            + "trusting the negative below.");
    }

    /// <summary>
    /// The BC shape the fix rests on: opening a TestPage does not enter a transaction world,
    /// so it does not commit. If a BC service update ever routes NavTestPage.Open through one,
    /// the runner's rollback boundary genuinely would need to move and this fails loudly rather
    /// than the corpus quietly disagreeing on one leg.
    /// </summary>
    [SkippableTheory]
    [InlineData("Microsoft.Dynamics.Nav.Runtime.NavTestPage")]
    [InlineData("Microsoft.Dynamics.Nav.Runtime.NavTestPageBase")]
    public void TestPageOpen_DoesNotEnterATransactionWorld(string typeFullName)
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var sites = CallSitesOf(Ncl(), typeFullName,
            BeginTransactionWorld, BeginTransactionWorldAndTransaction);

        Assert.True(sites.Count == 0,
            $"{typeFullName} must not enter a transaction world — a TestPage attaches through "
            + "the test client session, and entering a transaction world is what commits the "
            + "caller's active transaction. Found: " + string.Join(", ", sites));
    }

    /// <summary>
    /// The runner-side half, and the one that actually regressed: MarkOpened must not discard
    /// the transaction snapshot. Read off the runner's own compiled IL rather than its source,
    /// so a call reintroduced through a helper is caught too.
    /// </summary>
    [Fact]
    public void MarkOpened_DoesNotDiscardTheTransactionSnapshot()
    {
        var module = AssemblyDefinition
            .ReadAssembly(typeof(AlRunner.Patches.RunnerTestPageState).Assembly.Location)
            .MainModule;

        var sites = CallSitesOf(module, "AlRunner.Patches.RunnerTestPageState",
            nameof(AlRunner.Patches.RecordPatches.MarkCommitPoint));

        Assert.True(sites.Count == 0,
            "RunnerTestPageState must not call RecordPatches.MarkCommitPoint — that clears the "
            + "snapshot RollbackToCommitPoint restores from, so every write a [Test] made before "
            + "it opened a TestPage becomes permanent: AutoRollback cannot undo it and neither "
            + "can a later asserterror (issue #2400). Found: " + string.Join(", ", sites));
    }

    /// <summary>
    /// Negative control for the test above: the same scan DOES find the commit points that are
    /// meant to be there, so "found none" in MarkOpened means the call is absent rather than
    /// the scan being blind. ALDatabase_ALCommit is AL's own <c>Commit()</c> and
    /// ResetWriteTransactionState is the per-test isolation boundary; both must keep marking.
    /// </summary>
    [Fact]
    public void TheRealCommitPointsAreStillMarked()
    {
        var module = AssemblyDefinition
            .ReadAssembly(typeof(AlRunner.Patches.RunnerTestPageState).Assembly.Location)
            .MainModule;

        var sites = CallSitesOf(module, "AlRunner.Patches.ALDatabasePatches",
            nameof(AlRunner.Patches.RecordPatches.MarkCommitPoint));

        Assert.Contains("AlRunner.Patches.ALDatabasePatches::ALDatabase_ALCommit", sites);
        Assert.Contains("AlRunner.Patches.ALDatabasePatches::ResetWriteTransactionState", sites);
    }
}
