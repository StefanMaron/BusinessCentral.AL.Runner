using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Runner-mechanism test for issue #3468: the write-transaction flag behind
/// <c>Database.IsInWriteTransaction()</c> must not survive a test-method boundary, so a later
/// test's guarded <c>Codeunit.Run</c> is not refused by a write the PREVIOUS test left
/// uncommitted.
///
/// The BEHAVIOURAL claim is plain BC behaviour and lives upstream (see the PR body's
/// <c>Corpus-PR:</c> line, codeunit 60878 "Test Write Tx Test Boundary"), per
/// .claude/rules/bc-behavior-tests-go-upstream.md. This test spawns the real runner against a
/// synthetic bundle so a regression in the runner's own boundary handling fails loudly here
/// without depending on the submodule pin having moved.
///
/// No Base Application dependency (.claude/rules/no-base-app-in-csharp-tests.md): each AL test
/// raises its own Error() carrying the observed state, and the runner's PASS/FAIL output is the
/// assertion surface.
/// </summary>
public class WriteTransactionTestBoundaryTests
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
    public void UncommittedWriteInAnEarlierTest_DoesNotLeaveTheNextTestInAWriteTransaction()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-writetx-boundary-3468");
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "b3468000-0000-4000-8000-000000003468",
          "name": "WriteTxBoundary3468",
          "publisher": "Repro3468",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62470, "to": 62479 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(root, "TxbProbe.al"), """
        table 62470 "TXB Probe"
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

        codeunit 62472 "TXB Runnable"
        {
            trigger OnRun()
            var
                Probe: Record "TXB Probe";
            begin
                Probe."Entry No." := 90;
                Probe.Insert();
            end;
        }

        // A second runnable with its own key, because a successful guarded Codeunit.Run
        // COMMITS its own transaction (corpus TestCodeunitRunWriteTransaction), so the row
        // codeunit 62472 writes is still there when the later test runs — re-running 62472
        // would fail on a duplicate key instead of measuring the boundary.
        codeunit 62473 "TXB Runnable Two"
        {
            trigger OnRun()
            var
                Probe: Record "TXB Probe";
            begin
                Probe."Entry No." := 93;
                Probe.Insert();
            end;
        }

        codeunit 62470 "TXB Tests"
        {
            Subtype = Test;
            TestPermissions = Disabled;

            // Declaration order IS the fixture. Each writing test below deliberately does not
            // Commit(), and the guarded-run test that follows it asserts the platform ended
            // that write transaction at the test-method boundary.

            [Test]
            [TransactionModel(TransactionModel::AutoRollback)]
            procedure A_AutoRollbackTestWritesWithoutCommitting()
            var
                Probe: Record "TXB Probe";
            begin
                Probe."Entry No." := 91;
                Probe.Insert();

                if not Database.IsInWriteTransaction() then
                    Error('TXB1 FAIL: an uncommitted Insert must open a write transaction inside the test that made it');
            end;

            [Test]
            [TransactionModel(TransactionModel::AutoRollback)]
            procedure B_WritesNothingAtAll()
            begin
                if Database.IsInWriteTransaction() then
                    Error('TXB2 FAIL: a test that writes nothing must not start inside a write transaction left by the previous test');
            end;

            [Test]
            [TransactionModel(TransactionModel::AutoRollback)]
            procedure C_GuardedCodeunitRunIsAllowed()
            var
                Runnable: Codeunit "TXB Runnable";
                Probe: Record "TXB Probe";
            begin
                if Database.IsInWriteTransaction() then
                    Error('TXB3 FAIL: no write transaction may be pending at the start of this test');

                if not Runnable.Run() then
                    Error('TXB3 FAIL: a guarded Codeunit.Run must be allowed here, got [%1]', GetLastErrorText());

                if not Probe.Get(90) then
                    Error('TXB3 FAIL: the run codeunit''s row must be visible after a successful guarded run');
            end;

            // The same claim for the DEFAULT transaction model, where BC ends the transaction
            // by committing rather than by rolling back.
            [Test]
            procedure D_DefaultModelTestWritesWithoutCommitting()
            var
                Probe: Record "TXB Probe";
            begin
                Probe."Entry No." := 92;
                Probe.Insert();

                if not Database.IsInWriteTransaction() then
                    Error('TXB4 FAIL: an uncommitted Insert must open a write transaction inside the test that made it');
            end;

            [Test]
            procedure E_GuardedCodeunitRunIsStillAllowed()
            var
                Runnable: Codeunit "TXB Runnable Two";
                Probe: Record "TXB Probe";
            begin
                if Database.IsInWriteTransaction() then
                    Error('TXB5 FAIL: the previous test''s uncommitted write must have been committed at the test-method boundary');

                if not Runnable.Run() then
                    Error('TXB5 FAIL: a guarded Codeunit.Run must be allowed here, got [%1]', GetLastErrorText());

                if not Probe.Get(93) then
                    Error('TXB5 FAIL: the run codeunit''s row must be visible after a successful guarded run');

                // The previous test ran under the default model, so BC COMMITTED its write —
                // the row is still there. This is the negative direction of the same boundary:
                // ending the transaction must not mean discarding the rows.
                if not Probe.Get(92) then
                    Error('TXB5 FAIL: a default-model test''s write is committed at the boundary, so the row must still be visible');
            end;
        }
        """);

        var (output, exitCode) = RunRunner(root);

        Assert.True(exitCode == 0,
            $"Expected all five tests to pass (exit 0); got exit {exitCode}.\n{output}");
        Assert.DoesNotContain("FAIL", output);
        Assert.Contains("PASS  Codeunit62470.A_AutoRollbackTestWritesWithoutCommitting", output);
        Assert.Contains("PASS  Codeunit62470.B_WritesNothingAtAll", output);
        Assert.Contains("PASS  Codeunit62470.C_GuardedCodeunitRunIsAllowed", output);
        Assert.Contains("PASS  Codeunit62470.D_DefaultModelTestWritesWithoutCommitting", output);
        Assert.Contains("PASS  Codeunit62470.E_GuardedCodeunitRunIsStillAllowed", output);
    }
}
