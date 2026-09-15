using System.Diagnostics;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Runner-mechanism tests for issue #3773: a guarded <c>Codeunit.Run</c> whose <c>OnRun</c>
/// called <c>Commit()</c> and then errored lost the committed row.
///
/// The BEHAVIOURAL claim — where a failed guarded run's rollback stops when the run committed
/// partway through — is plain BC behaviour and is adjudicated upstream, by the arms this
/// change adds to <c>TestCodeunitRunGuard.al</c> (corpus codeunit 60217). This file is the
/// sibling of <see cref="CodeunitRunGuardRollbackTests"/>, written for #2334 in the same
/// shape and for the same stated reason: the shape reproduces in the unit legs so a
/// regression fails here within minutes rather than only on a corpus leg.
///
/// The second test is the half with no upstream twin. Nothing an AL test can observe
/// distinguishes the runner's two internal commit entry points, and only one of them may
/// clear the open transaction-world scopes.
///
/// No Library Assert dependency and no <c>"application"</c> in the fixture's app.json
/// (.claude/rules/no-base-app-in-csharp-tests.md): each AL test raises its own Error() with
/// the observed value, so the runner's own PASS/FAIL output is the assertion surface.
/// </summary>
public class CodeunitRunCommitBoundaryTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static (string output, int exit) RunRunner(params string[] bundles)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        foreach (var b in bundles) args.Append(" \"").Append(b).Append('"');
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
        if (!p.WaitForExit(180_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    [SkippableFact]
    public void GuardedRun_CommitThenError_KeepsTheCommittedRowAndRollsBackTheRest()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-codeunit-run-commit-boundary-3773");
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "b3773000-0000-4000-8000-000000003773",
          "name": "CodeunitRunCommitBoundary3773",
          "publisher": "Repro3773",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 63773, "to": 63777 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(root, "CrcProbe.al"), """
        table 63773 "CRC Probe"
        {
            DataClassification = SystemMetadata;

            fields
            {
                field(1; "Entry No."; Integer) { }
            }

            keys
            {
                key(PK; "Entry No.") { Clustered = true; }
            }
        }

        // Writes 1, commits it, writes 2, then errors — so one guarded run exercises both
        // sides of the rollback boundary the Commit() establishes.
        codeunit 63774 "CRC Commit Then Error"
        {
            trigger OnRun()
            var
                Probe: Record "CRC Probe";
            begin
                Probe.Init();
                Probe."Entry No." := 1;
                Probe.Insert();

                Commit();

                Probe.Init();
                Probe."Entry No." := 2;
                Probe.Insert();

                Error('CRC-BOOM-AFTER-COMMIT');
            end;
        }

        // The control: no Commit() anywhere, so the whole run must roll back (#2334).
        codeunit 63775 "CRC Write Then Error"
        {
            trigger OnRun()
            var
                Probe: Record "CRC Probe";
            begin
                Probe.Init();
                Probe."Entry No." := 1;
                Probe.Insert();
                Error('CRC-BOOM-NO-COMMIT');
            end;
        }

        codeunit 63776 "CRC Tests"
        {
            Subtype = Test;
            TestPermissions = Disabled;

            local procedure Initialize()
            var
                Probe: Record "CRC Probe";
            begin
                Probe.DeleteAll();
                Commit();
            end;

            [Test]
            procedure InstanceForm_CommitThenError_KeepsCommittedRowOnly()
            var
                Probe: Record "CRC Probe";
                Worker: Codeunit "CRC Commit Then Error";
                Ok: Boolean;
            begin
                Initialize();

                Ok := Worker.Run();
                if Ok then
                    Error('CRC1 FAIL: guarded instance Run() must return false on an erroring OnRun.');

                Probe.Reset();
                if not Probe.Get(1) then
                    Error('CRC1 FAIL: the row committed before the Error() must survive the failed run.');
                if Probe.Get(2) then
                    Error('CRC1 FAIL: the row written after the Commit() must still be rolled back.');
            end;

            [Test]
            procedure StaticForm_CommitThenError_KeepsCommittedRowOnly()
            var
                Probe: Record "CRC Probe";
                Ok: Boolean;
            begin
                Initialize();

                Ok := Codeunit.Run(Codeunit::"CRC Commit Then Error");
                if Ok then
                    Error('CRC2 FAIL: guarded static Codeunit.Run must return false on an erroring OnRun.');

                Probe.Reset();
                if not Probe.Get(1) then
                    Error('CRC2 FAIL: the row committed before the Error() must survive the failed run.');
                if Probe.Get(2) then
                    Error('CRC2 FAIL: the row written after the Commit() must still be rolled back.');
            end;

            // Must-not-regress control for #2334: with no Commit() inside the run, the
            // guarded run's own write is still discarded in full.
            [Test]
            procedure InstanceForm_WriteThenError_NoCommit_RollsBackEverything()
            var
                Probe: Record "CRC Probe";
                Worker: Codeunit "CRC Write Then Error";
                Ok: Boolean;
            begin
                Initialize();

                Ok := Worker.Run();
                if Ok then
                    Error('CRC3 FAIL: guarded instance Run() must return false on an erroring OnRun.');

                Probe.Reset();
                if Probe.Count() <> 0 then
                    Error('CRC3 FAIL: expected Count()=0 after an uncommitted write-then-error run, got %1', Probe.Count());
            end;

            // Must-not-regress control: the CALLER's own row, written and committed before
            // the guarded run, is not the failed scope's to undo. The write must be
            // COMMITTED — BC refuses a guarded Codeunit.Run outright while the caller holds
            // an uncommitted write ("the transaction is stopped"), which is why the #2334
            // sibling of this arm commits too.
            [Test]
            procedure CallerCommittedWrite_SurvivesInnerRunFailure()
            var
                Probe: Record "CRC Probe";
                Ok: Boolean;
            begin
                Initialize();

                Probe.Init();
                Probe."Entry No." := 9;
                Probe.Insert();
                Commit();

                Ok := Codeunit.Run(Codeunit::"CRC Commit Then Error");
                if Ok then
                    Error('CRC4 FAIL: guarded static Codeunit.Run must return false on an erroring OnRun.');

                Probe.Reset();
                if not Probe.Get(9) then
                    Error('CRC4 FAIL: the caller''s own committed row must not be undone by the inner run''s rollback.');
                if not Probe.Get(1) then
                    Error('CRC4 FAIL: the inner run''s committed row must survive.');
                if Probe.Get(2) then
                    Error('CRC4 FAIL: the inner run''s post-commit row must still be rolled back.');
            end;
        }
        """);

        var (output, exitCode) = RunRunner(root);

        Assert.True(exitCode == 0,
            $"Expected all four commit-boundary tests to pass (exit 0); got exit {exitCode}.\n{output}");
        Assert.DoesNotContain("FAIL", output);
        Assert.Contains("PASS  Codeunit63776.InstanceForm_CommitThenError_KeepsCommittedRowOnly", output);
        Assert.Contains("PASS  Codeunit63776.StaticForm_CommitThenError_KeepsCommittedRowOnly", output);
        Assert.Contains("PASS  Codeunit63776.InstanceForm_WriteThenError_NoCommit_RollsBackEverything", output);
        Assert.Contains("PASS  Codeunit63776.CallerCommittedWrite_SurvivesInnerRunFailure", output);
    }

    /// <summary>
    /// The half with no upstream twin: which of the runner's two commit entry points is
    /// allowed to clear the open transaction-world scopes.
    ///
    /// <c>MarkExplicitCommitPoint</c> clears every scope on the stack, which is right for AL's
    /// <c>Commit()</c> statement and wrong for <c>EndGuardedRunTransaction</c>'s committing
    /// half: that one has already popped its own scope and narrowed the enclosing scopes to
    /// just the keys it touched, so clearing them wholesale would make an enclosing scope's
    /// writes to unrelated tables durable too. Read off the runner's compiled IL rather than
    /// its source, so a call reintroduced through a helper is caught as well.
    /// </summary>
    [Fact]
    public void OnlyTheAlCommitStatementClearsTheOpenTransactionWorldScopes()
    {
        var module = AssemblyDefinition
            .ReadAssembly(typeof(AlRunner.Patches.ALDatabasePatches).Assembly.Location)
            .MainModule;

        var callers = CallersOf(module, "AlRunner.Patches.RecordPatches",
            nameof(AlRunner.Patches.RecordPatches.MarkExplicitCommitPoint));

        Assert.Contains("AlRunner.Patches.ALDatabasePatches::CommitWithoutTestExecutionGuard", callers);
        Assert.DoesNotContain("AlRunner.Patches.ALDatabasePatches::EndGuardedRunTransaction", callers);

        // Negative control: the same scan finds the plain marker where it is still expected,
        // so "does not contain" above means the call is absent rather than the scan blind.
        var plain = CallersOf(module, "AlRunner.Patches.RecordPatches",
            nameof(AlRunner.Patches.RecordPatches.MarkCommitPoint));

        Assert.Contains("AlRunner.Patches.ALDatabasePatches::CommitWithoutTestExecutionGuard", plain);
        Assert.Contains("AlRunner.Patches.RecordPatches::MarkExplicitCommitPoint", plain);
    }

    /// <summary>Every method in <paramref name="module"/> whose body calls
    /// <paramref name="declaringType"/>.<paramref name="methodName"/>, as
    /// <c>Namespace.Type::Method</c>.</summary>
    private static List<string> CallersOf(ModuleDefinition module, string declaringType, string methodName)
    {
        var found = new List<string>();
        foreach (var type in module.GetTypes())
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                foreach (var ins in method.Body.Instructions)
                {
                    if (ins.OpCode != OpCodes.Call && ins.OpCode != OpCodes.Callvirt) continue;
                    if (ins.Operand is not MethodReference target) continue;
                    if (target.Name != methodName) continue;
                    if (target.DeclaringType?.FullName != declaringType) continue;
                    found.Add($"{type.FullName}::{method.Name}");
                    break;
                }
            }
        return found;
    }
}
