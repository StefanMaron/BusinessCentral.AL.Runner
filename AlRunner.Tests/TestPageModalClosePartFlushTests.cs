// TestPageModalClosePartFlushTests — issue #3682. The modal close route reaches the part
// flush through Dispose(), and nothing used to fail if that flush were removed.
//
// WHAT REAL BC DOES, AND WHO MEASURED IT
// --------------------------------------
// BC's two close routes AGREE about an uncommitted subpage-part row: both persist it.
// Measured on a real service tier by corpus codeunit 60420 "TPMF Tests"
// (StefanMaron/BusinessCentral.AL.Language.Tests#311, merged 22e226c4), green on all eight
// cloud legs of run 34345468218 with the tests confirmed to have EXECUTED — eight distinct
// ModalPartFlush_ PASS names per leg, per tools/corpus-pass-count.py. The decisive arms are
// the ones that invoke no action at all (F/G/H), because invoking OK is itself a write-back
// moment and would otherwise measure the action rather than the close.
//
// So the runner already matches BC here and this file adds NO behaviour. It exists because
// the way the runner matches is easy to delete by accident.
//
// THE RUNNER MECHANISM THIS PINS
// ------------------------------
// TestPage.Close() flushes the parts directly. The modal route does not: BC wraps a
// [ModalPageHandler] in a scope, and when the refcount on the page handle drops the platform
// disposes it, which reaches LiveNavTestPage.Dispose(). Measured stack, this repo, BC 28.1:
//
//   NavTestExecution.TestHandleModalForm -> NavTestPageHandle.Dispose
//     -> TreeObjectReferenceHandler.Dispose -> NavTestPage.Dispose
//       -> LiveNavTestPage.Dispose -> FlushParts -> <part>.FlushRow -> FlushPendingNewRow
//
// WHY ONE TEST IS NOT ENOUGH HERE, WHICH IS THE WHOLE POINT
// ---------------------------------------------------------
// Dispose() is `FlushParts(); FlushRow();` and those two are REDUNDANT for a part row on
// this route: the host's FlushParts() reaches each part's FlushRow(), and each part page has
// its own Dispose() that calls its own FlushRow(). Measured by removing each in turn and
// running the AL arm below (BC 28.1.49838.53910):
//
//   Dispose() { FlushParts(); FlushRow(); }   PASS   (main)
//   Dispose() { FlushRow(); }                 PASS   <- FlushParts removed, still green
//   Dispose() { FlushParts(); }               PASS   <- FlushRow removed, still green
//   Dispose() { }                             FAIL
//
// An AL test alone therefore CANNOT catch the removal of either call on its own — which is
// exactly the silent single-edit regression #3682 asked to be guarded against. Hence two
// halves, and neither is redundant:
//
//   * ModalCloseWithoutInvoke_PersistsThePartRow — the behavioural claim. Fails when the
//     flush is gone from the route altogether.
//   * DisposeMustFlushBothPartsAndItsOwnRow — reads the SHIPPED IL and fails when EITHER
//     call is removed on its own.
//
// The IL half reads IL rather than source for the reason OutOfScopePointerCallSiteGuardTests
// gives: IL carries no comments, sees every spelling of a call, and measures what ships.
// Note what it does NOT do: `if (x) return;` in front of a call leaves the `call` in IL, so
// this guard is about the call EXISTING, not about it being reached. The AL half is what
// covers reachability, and the pair is why both are here.
using System.Diagnostics;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageModalClosePartFlushTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public TestPageModalClosePartFlushTests()
    {
        _root = TestScratch.Dir("al-runner-testpage-modal-close-part-flush");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static string[] ExtraPackageCacheArgs()
    {
        var platformApps = TestArtifacts.PlatformAppsDir();
        return Directory.Exists(platformApps)
            ? new[] { "--package-cache", platformApps }
            : Array.Empty<string>();
    }

    /// <summary>
    /// A host Card with a CardPart over its own table, driven through RunModal with a
    /// [ModalPageHandler] that writes a part row and invokes NOTHING.
    ///
    /// The part is a CardPart, not a ListPart, and that is load-bearing rather than
    /// incidental: LiveNavTestPage.InsertOnCompletePrimaryKey writes a row EAGERLY once its
    /// primary key is complete, but only on a page whose type makes
    /// RunnerPageInstance.WritesRowsAsTheyAreCompleted true — List, ListPart, Worksheet. A
    /// ListPart here would insert the row at SetValue time and the arm would measure the
    /// eager insert while appearing to measure the close. Measured: with a ListPart, emptying
    /// Dispose() entirely leaves this arm green.
    ///
    /// Invoking nothing is the other half of the isolation, and is the same confound corpus
    /// arms F/G/H were written to remove: OK().Invoke() reaches SaveCurrentRow(), which is
    /// FlushParts(); FlushRow(), so an arm ending in OK persists through the ACTION and says
    /// nothing about the close.
    /// </summary>
    private void WriteBundle()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "app.json"), """
        {
          "id": "a7b8c9d0-1e2f-4a3b-8c4d-5e6f7a8b9c04",
          "name": "Runner Mechanism - TestPage Modal Close Part Flush",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62770, "to": 62779 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(_root, "MpfLine.Table.al"), """
        table 62770 "Mpf Line"
        {
            DataClassification = CustomerContent;

            fields
            {
                field(1; "Head No."; Code[20]) { }
                field(2; "Line No."; Integer) { }
                field(3; Descr; Text[50]) { }
            }

            keys
            {
                key(PK; "Head No.", "Line No.") { Clustered = true; }
            }
        }
        """);

        File.WriteAllText(Path.Combine(_root, "MpfHead.Table.al"), """
        table 62771 "Mpf Head"
        {
            DataClassification = CustomerContent;

            fields
            {
                field(1; "No."; Code[20]) { }
                field(2; Descr; Text[50]) { }
            }

            keys
            {
                key(PK; "No.") { Clustered = true; }
            }
        }
        """);

        File.WriteAllText(Path.Combine(_root, "MpfPart.Page.al"), """
        page 62772 "Mpf Part"
        {
            PageType = CardPart;
            SourceTable = "Mpf Line";
            ApplicationArea = All;
            InsertAllowed = true;

            layout
            {
                area(Content)
                {
                    group(Detail)
                    {
                        field(HeadNo; Rec."Head No.") { ApplicationArea = All; }
                        field(LineNo; Rec."Line No.") { ApplicationArea = All; }
                        field(Descr; Rec.Descr) { ApplicationArea = All; }
                    }
                }
            }
        }
        """);

        File.WriteAllText(Path.Combine(_root, "MpfCard.Page.al"), """
        page 62773 "Mpf Card"
        {
            PageType = Card;
            SourceTable = "Mpf Head";
            ApplicationArea = All;

            layout
            {
                area(Content)
                {
                    group(General)
                    {
                        field(Descr; Rec.Descr) { ApplicationArea = All; }
                    }
                    part(Lines; "Mpf Part") { ApplicationArea = All; }
                }
            }
        }
        """);

        File.WriteAllText(Path.Combine(_root, "MpfTests.Codeunit.al"), """
        codeunit 62774 "Mpf Tests"
        {
            Subtype = Test;

            local procedure SeedHead()
            var
                Head: Record "Mpf Head";
                Line: Record "Mpf Line";
            begin
                Line.DeleteAll();
                Head.DeleteAll();
                Head.Init();
                Head."No." := 'H1';
                Head.Descr := 'SeededHead';
                Head.Insert();
            end;

            [Test]
            [HandlerFunctions('WritePartNoInvokeHandler')]
            procedure ModalCloseWithoutInvoke_PersistsThePartRow()
            var
                Line: Record "Mpf Line";
                Card: Page "Mpf Card";
            begin
                SeedHead();
                if not Line.IsEmpty() then
                    Error('precondition: the part table must start empty, otherwise a leftover row would satisfy the assertion below');

                Card.RunModal();

                if not Line.Get('H1', 30000) then
                    Error('the platform-driven modal close must write the part''s uncommitted New() row back — real BC does, per corpus 60420 arm F');
                if Line.Descr <> 'NoInvokePart' then
                    Error('the value written through the part must reach the table, got: ' + Line.Descr);
            end;

            [Test]
            procedure ModalCloseWithoutInvoke_HandlerThatWritesNothing_LeavesThePartTableEmpty()
            var
                Line: Record "Mpf Line";
                Card: TestPage "Mpf Card";
            begin
                SeedHead();

                Card.OpenEdit();
                Card.Close();

                if not Line.IsEmpty() then
                    Error('a close must not invent a part row when the handler wrote nothing');
            end;

            [ModalPageHandler]
            procedure WritePartNoInvokeHandler(var Card: TestPage "Mpf Card")
            begin
                Card.Lines.New();
                Card.Lines.HeadNo.SetValue('H1');
                Card.Lines.LineNo.SetValue(30000);
                Card.Lines.Descr.SetValue('NoInvokePart');
            end;
        }
        """);
    }

    private (string output, int exit) RunBundled()
    {
        var args = new StringBuilder(
            TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg + $" \"{_root}\"");
        foreach (var a in ExtraPackageCacheArgs()) args.Append($" \"{a}\"");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(600_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    /// <summary>
    /// Positive plus its negative, in one bundle: the modal close persists the part row the
    /// handler wrote, and a close whose handler wrote nothing leaves the table empty. The
    /// negative is what stops the positive passing for the wrong reason — an implementation
    /// that inserted a row on every close would satisfy the first assertion alone.
    ///
    /// RED/GREEN proof (BC 28.1.49838.53910, this box): emptying LiveNavTestPage.Dispose() to
    /// `{ }` makes ModalCloseWithoutInvoke_PersistsThePartRow fail — Line.Get('H1', 30000)
    /// answers false, because nothing on the modal route ever flushes the part buffer.
    /// </summary>
    [SkippableFact]
    public void ModalCloseWithoutInvoke_PersistsThePartRow()
    {
        TestArtifacts.SkipIfMissing();

        WriteBundle();
        var (output, exit) = RunBundled();

        Assert.True(exit == 0, $"Expected the bundle to pass; exit={exit}\n{output}");
        Assert.Contains("PASS  Codeunit62774.ModalCloseWithoutInvoke_PersistsThePartRow", output);
        Assert.Contains(
            "PASS  Codeunit62774.ModalCloseWithoutInvoke_HandlerThatWritesNothing_LeavesThePartTableEmpty",
            output);
        Assert.DoesNotContain("FAIL", output);
    }

    /// <summary>
    /// LiveNavTestPage.Dispose() must call BOTH FlushParts and FlushRow.
    ///
    /// This is the half the AL arm cannot cover. The two calls are redundant for a part row
    /// on the modal route — the host's FlushParts() reaches each part's FlushRow(), and each
    /// part page's own Dispose() calls its own FlushRow() — so removing either ALONE leaves
    /// every behavioural arm green, and only removing both turns anything red. Measured, all
    /// four combinations, in this file's header.
    ///
    /// The host row is the other reason FlushRow() must stay: a Card's own edited record is
    /// flushed by FlushRow() and by nothing else on this route.
    ///
    /// RED/GREEN proof: deleting either call from Dispose() and rebuilding fails this test
    /// naming the missing one.
    /// </summary>
    [Fact]
    public void DisposeMustFlushBothPartsAndItsOwnRow()
    {
        var path = typeof(AlRunner.Infrastructure.RunnerOutOfScopeException).Assembly.Location;
        using var asm = AssemblyDefinition.ReadAssembly(path);

        var type = asm.MainModule.GetTypes().SingleOrDefault(t => t.Name == "LiveNavTestPage");
        Assert.True(type != null,
            "LiveNavTestPage was not found in the shipped assembly — this guard cannot have "
            + "measured anything. If the type was renamed, retarget the guard rather than deleting it.");

        var dispose = type!.Methods.SingleOrDefault(m =>
            m.Name == "Dispose" && m.HasBody && m.Parameters.Count == 0);
        Assert.True(dispose != null,
            "LiveNavTestPage has no parameterless Dispose() with a body. The modal close route "
            + "reaches the part flush through it (issue #3682); a Dispose that no longer exists "
            + "means that route flushes nothing.");

        var called = dispose!.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            .Select(i => (i.Operand as MethodReference)?.Name)
            .Where(n => n != null)
            .ToHashSet()!;

        Assert.True(called.Contains("FlushParts"),
            "LiveNavTestPage.Dispose() no longer calls FlushParts(). That is the call the "
            + "platform-driven modal close relies on to write an uncommitted subpage-part row "
            + $"back. Calls found: {string.Join(", ", called.OrderBy(n => n))}. Real BC persists "
            + "that row (corpus 60420, StefanMaron/BusinessCentral.AL.Language.Tests#311, green "
            + "on all eight cloud legs), so removing this makes the runner answer differently "
            + "from BC. See issue #3682.");

        Assert.True(called.Contains("FlushRow"),
            "LiveNavTestPage.Dispose() no longer calls FlushRow(). That is the call that writes "
            + "the page's OWN edited record back on the modal close route, and — reached through "
            + "each part page's own Dispose() — the part's row as well. "
            + $"Calls found: {string.Join(", ", called.OrderBy(n => n))}. See issue #3682.");
    }
}
