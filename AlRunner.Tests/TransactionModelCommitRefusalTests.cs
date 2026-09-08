using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Runner-mechanism test for issue #3451: an explicit <c>Commit()</c> issued while a
/// <c>[TransactionModel(TransactionModel::AutoRollback)]</c> test method is in force must be
/// refused, the way BC's own <c>ALDatabase.ALCommit</c> refuses it before it reaches the
/// <c>switch (session.CommitBehavior)</c> that #3449 mirrored.
///
/// The BEHAVIOURAL claim is plain BC behaviour and lives upstream —
/// StefanMaron/BusinessCentral.AL.Language.Tests#278, codeunit 60899
/// "Test TxModel AutoRollback", per .claude/rules/bc-behavior-tests-go-upstream.md, where all
/// five arms were measured on a real BC 28.4 service tier. This test spawns the real runner
/// against a synthetic bundle so a regression in the runner's own guard fails loudly here
/// without depending on the submodule pin having moved yet.
///
/// No Library Assert / Base Application dependency (no "application" in the fixture's
/// app.json — see .claude/rules/no-base-app-in-csharp-tests.md): each test raises its own
/// Error() carrying the observed state, and the runner's PASS/FAIL output is the assertion
/// surface.
/// </summary>
public class TransactionModelCommitRefusalTests
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
    public void AutoRollbackTest_ExplicitCommit_IsRefusedWithBcOwnText()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-txmodel-commit-3451");
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "b3451000-0000-4000-8000-000000003451",
          "name": "TxModelCommit3451",
          "publisher": "Repro3451",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62460, "to": 62469 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(root, "TxmProbe.al"), """
        table 62460 "TXM Probe"
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

        codeunit 62461 "TXM Helpers"
        {
            internal procedure PlainCommit()
            begin
                Commit();
            end;

            [CommitBehavior(CommitBehavior::Ignore)]
            internal procedure InsertAndCommitIgnored(EntryNo: Integer)
            var
                Probe: Record "TXM Probe";
            begin
                Probe."Entry No." := EntryNo;
                Probe.Insert();
                Commit();
            end;

            [CommitBehavior(CommitBehavior::Error)]
            internal procedure CommitUnderErrorBehavior()
            begin
                Commit();
            end;
        }

        codeunit 62462 "TXM Runnable"
        {
            trigger OnRun()
            var
                Probe: Record "TXM Probe";
            begin
                Probe."Entry No." := 20;
                Probe.Insert();
            end;
        }

        codeunit 62460 "TXM Tests"
        {
            Subtype = Test;
            TestPermissions = Disabled;

            var
                Helpers: Codeunit "TXM Helpers";
                RefusalTxt: Label 'Tests cannot call the Commit function if TransactionModel property is set to AutoRollback.', Locked = true;

            // The guard is BC's, and BC writes it into ALDatabase.ALCommit only. A guarded
            // Codeunit.Run ends its nested transaction through EndTransactionWorldAndTransaction
            // instead, which is not an AL Commit() statement and is not refused — so the runner
            // must not route that internal commit through the refusal.
            [Test]
            [TransactionModel(TransactionModel::AutoRollback)]
            procedure AutoRollback_GuardedCodeunitRunIsNotRefused()
            var
                Runnable: Codeunit "TXM Runnable";
                Probe: Record "TXM Probe";
            begin
                // No write before the call: a guarded Codeunit.Run whose result is consumed
                // opens a transaction world, and BC refuses that while the caller has an
                // uncommitted write pending — a different rule (corpus TestCodeunitRunWrite-
                // Transaction), and one this test must not trip over. Entry 20 is used here
                // and nowhere else in this bundle.
                if not Runnable.Run() then
                    Error('TXM7 FAIL: a guarded Codeunit.Run inside an AutoRollback test must succeed, got [%1]', GetLastErrorText());

                if not Probe.Get(20) then
                    Error('TXM7 FAIL: the run codeunit''s row must be visible after a successful guarded run');
            end;

            // Arm (a): the refusal itself.
            [Test]
            [TransactionModel(TransactionModel::AutoRollback)]
            procedure AutoRollback_ExplicitCommitIsRefused()
            var
                ErrText: Text;
            begin
                asserterror Commit();
                ErrText := GetLastErrorText();
                if StrPos(ErrText, RefusalTxt) = 0 then
                    Error('TXM1 FAIL: expected the AutoRollback refusal, got [%1]', ErrText);
            end;

            // Arm (b): session state, not a lexical property of the Commit() statement — the
            // Commit() here sits in a callee carrying no attribute of its own.
            [Test]
            [TransactionModel(TransactionModel::AutoRollback)]
            procedure AutoRollback_RefusalReachesAnUnattributedCallee()
            var
                ErrText: Text;
            begin
                asserterror Helpers.PlainCommit();
                ErrText := GetLastErrorText();
                if StrPos(ErrText, RefusalTxt) = 0 then
                    Error('TXM2 FAIL: the refusal must follow the executing test method into an unattributed callee, got [%1]', ErrText);
            end;

            // Arm (c), the control: under AutoCommit the same Commit() is allowed AND is a
            // real commit. Without this the guard could "work" by refusing every Commit().
            [Test]
            [TransactionModel(TransactionModel::AutoCommit)]
            procedure AutoCommit_ExplicitCommitIsAllowedAndDurable()
            var
                Probe: Record "TXM Probe";
            begin
                Probe.DeleteAll();
                Commit();

                Probe."Entry No." := 10;
                Probe.Insert();
                Commit();

                asserterror Error('unrelated');

                Clear(Probe);
                if not Probe.Get(10) then
                    Error('TXM3 FAIL: under AutoCommit the Commit() must be allowed and durable — the row must survive the unrelated error');

                Probe.Delete();
                Commit();
            end;

            // Arm (d): CommitBehavior::Ignore exempts the refusal — BC's guard has
            // `session.CommitBehavior != CommitBehavior.Ignore` as its third condition. The
            // Commit() is then neither refused nor performed.
            [Test]
            [TransactionModel(TransactionModel::AutoRollback)]
            procedure AutoRollback_CommitBehaviorIgnoreIsExempt()
            var
                Probe: Record "TXM Probe";
            begin
                Helpers.InsertAndCommitIgnored(11);

                asserterror Error('unrelated');

                if Probe.Get(11) then
                    Error('TXM4 FAIL: an ignored Commit() must not move the rollback boundary, so the unrelated error must undo the Insert');
            end;

            // Arm (e): both guards apply, and the TransactionModel one is evaluated first, so
            // its message is the one AL sees — not Lang.CommitProhibited.
            [Test]
            [TransactionModel(TransactionModel::AutoRollback)]
            procedure AutoRollback_RefusalOutranksCommitBehaviorError()
            var
                ErrText: Text;
            begin
                asserterror Helpers.CommitUnderErrorBehavior();
                ErrText := GetLastErrorText();

                if StrPos(ErrText, RefusalTxt) = 0 then
                    Error('TXM5 FAIL: expected the AutoRollback refusal to win, got [%1]', ErrText);
                if StrPos(ErrText, 'Commit is prohibited in the current scope') > 0 then
                    Error('TXM5 FAIL: the CommitBehavior::Error message must not be the one raised, got [%1]', ErrText);
            end;

            // The guard must be scoped to the executing AutoRollback test method and nothing
            // wider: a test with no TransactionModel attribute commits normally, which is what
            // most of the corpus does.
            [Test]
            procedure Unattributed_ExplicitCommitIsAllowed()
            var
                Probe: Record "TXM Probe";
            begin
                Probe.DeleteAll();
                Probe."Entry No." := 12;
                Probe.Insert();
                Commit();

                asserterror Error('unrelated');

                Clear(Probe);
                if not Probe.Get(12) then
                    Error('TXM6 FAIL: a [Test] with no TransactionModel attribute must commit normally');

                Probe.Delete();
                Commit();
            end;
        }
        """);

        var (output, exitCode) = RunRunner(root);

        Assert.True(exitCode == 0,
            $"Expected all seven tests to pass (exit 0); got exit {exitCode}.\n{output}");
        Assert.DoesNotContain("FAIL", output);
        Assert.Contains("PASS  Codeunit62460.AutoRollback_ExplicitCommitIsRefused", output);
        Assert.Contains("PASS  Codeunit62460.AutoRollback_RefusalReachesAnUnattributedCallee", output);
        Assert.Contains("PASS  Codeunit62460.AutoCommit_ExplicitCommitIsAllowedAndDurable", output);
        Assert.Contains("PASS  Codeunit62460.AutoRollback_CommitBehaviorIgnoreIsExempt", output);
        Assert.Contains("PASS  Codeunit62460.AutoRollback_RefusalOutranksCommitBehaviorError", output);
        Assert.Contains("PASS  Codeunit62460.AutoRollback_GuardedCodeunitRunIsNotRefused", output);
        Assert.Contains("PASS  Codeunit62460.Unattributed_ExplicitCommitIsAllowed", output);
    }
}
