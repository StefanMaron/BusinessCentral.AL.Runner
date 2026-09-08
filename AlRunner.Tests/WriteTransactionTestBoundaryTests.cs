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

    /// <summary>
    /// Issue #3480, the other end of the same boundary. BC handles
    /// <c>TransactionModel::None</c> BEFORE the method body — the pre-body
    /// <c>while (IsTransactionActive()) EndTransaction(commit: false)</c> loop in
    /// <c>NavTestCodeunit.ExecuteTestMethodAsync</c> — so a None test body runs with no
    /// transaction at all, and a write from that body is refused by
    /// <c>TransactionManager.EnsureWriteTransactionStarted</c>'s <c>ThrowIfNoTransaction()</c>.
    /// A write inside a codeunit the test RUNS is fine, because both forms of
    /// <c>Codeunit.Run</c> begin a transaction of their own.
    ///
    /// The BC claim itself is pinned upstream (corpus 60878 Test08-Test10, PR body's
    /// <c>Corpus-PR:</c> line). This pins the runner's own mechanism.
    /// </summary>
    [SkippableFact]
    public void UnderTransactionModelNone_ATestBodyHasNoTransactionAndCannotWrite()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-writetx-none-3480");
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "b3480000-0000-4000-8000-000000003480",
          "name": "WriteTxNone3480",
          "publisher": "Repro3480",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62480, "to": 62489 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(root, "TxnProbe.al"), """
        table 62480 "TXN Probe"
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

        codeunit 62482 "TXN Runnable"
        {
            trigger OnRun()
            var
                Probe: Record "TXN Probe";
            begin
                Probe."Entry No." := 80;
                Probe.Insert();
            end;
        }

        // The statement-form target. Its own key, because the guarded run above commits its
        // row and a duplicate key inside a run would return `false` for a reason that has
        // nothing to do with the transaction.
        codeunit 62483 "TXN Runnable Two"
        {
            trigger OnRun()
            var
                Probe: Record "TXN Probe";
            begin
                Probe."Entry No." := 83;
                Probe.Insert();
            end;
        }

        codeunit 62481 "TXN Tests"
        {
            Subtype = Test;
            TestPermissions = Disabled;

            // Declaration order IS the fixture: F writes without committing, and everything
            // after it runs under TransactionModel::None.

            [Test]
            procedure F_DefaultModelTestWritesWithoutCommitting()
            var
                Probe: Record "TXN Probe";
            begin
                Probe."Entry No." := 81;
                Probe.Insert();

                if not Database.IsInWriteTransaction() then
                    Error('TXN1 FAIL: an uncommitted Insert must open a write transaction inside the test that made it');
            end;

            [Test]
            [TransactionModel(TransactionModel::None)]
            procedure G_NoneTestStartsWithNoTransaction()
            var
                Runnable: Codeunit "TXN Runnable";
                Probe: Record "TXN Probe";
            begin
                if Database.IsInWriteTransaction() then
                    Error('TXN2 FAIL: a None test body runs with no transaction, so no write transaction may be pending');

                if not Probe.Get(81) then
                    Error('TXN2 FAIL: ending the previous test''s transaction must not discard its committed row');

                if not Runnable.Run() then
                    Error('TXN2 FAIL: a guarded Codeunit.Run must be allowed here, got [%1]', GetLastErrorText());

                if not Probe.Get(80) then
                    Error('TXN2 FAIL: the run codeunit''s row must be visible after a successful guarded run');
            end;

            [Test]
            [TransactionModel(TransactionModel::None)]
            procedure H_NoneTestCannotWriteFromItsOwnBody()
            var
                Probe: Record "TXN Probe";
            begin
                Probe."Entry No." := 82;
                asserterror Probe.Insert();

                if GetLastErrorText() <> 'A transaction must be started before changes can be made to the database.' then
                    Error('TXN3 FAIL: expected BC''s no-transaction refusal, got [%1]', GetLastErrorText());

                if Database.IsInWriteTransaction() then
                    Error('TXN3 FAIL: a refused write must not open a write transaction');

                if Probe.Get(82) then
                    Error('TXN3 FAIL: a refused write must not have written a row');
            end;

            [Test]
            [TransactionModel(TransactionModel::None)]
            procedure I_ARunCodeunitMayWriteUnderNone()
            var
                Probe: Record "TXN Probe";
            begin
                Codeunit.Run(Codeunit::"TXN Runnable Two");

                if not Probe.Get(83) then
                    Error('TXN4 FAIL: Codeunit.Run begins a transaction of its own, so the run codeunit''s write must land');

                if Database.IsInWriteTransaction() then
                    Error('TXN4 FAIL: the transaction Codeunit.Run began must end with the run');
            end;

            // The scope must not outlive the None test that opened it: this default-model test
            // writes, which would be refused if it had.
            [Test]
            procedure J_ADefaultModelTestAfterANoneTestCanStillWrite()
            var
                Probe: Record "TXN Probe";
            begin
                Probe."Entry No." := 84;
                Probe.Insert();

                if not Database.IsInWriteTransaction() then
                    Error('TXN5 FAIL: a default-model test after a None test must still be able to write');
            end;
        }
        """);

        var (output, exitCode) = RunRunner(root);

        Assert.True(exitCode == 0,
            $"Expected all five tests to pass (exit 0); got exit {exitCode}.\n{output}");
        Assert.DoesNotContain("FAIL", output);
        Assert.Contains("PASS  Codeunit62481.F_DefaultModelTestWritesWithoutCommitting", output);
        Assert.Contains("PASS  Codeunit62481.G_NoneTestStartsWithNoTransaction", output);
        Assert.Contains("PASS  Codeunit62481.H_NoneTestCannotWriteFromItsOwnBody", output);
        Assert.Contains("PASS  Codeunit62481.I_ARunCodeunitMayWriteUnderNone", output);
        Assert.Contains("PASS  Codeunit62481.J_ADefaultModelTestAfterANoneTestCanStillWrite", output);
    }
    /// <summary>
    /// Issue #3543, the sibling surfaces. <c>Codeunit.Run</c> is not the only AL construct
    /// that begins a transaction, so the #3480 scope above refused writes BC allows: a report's
    /// dataitem trigger and a page field's <c>OnValidate</c> both run inside a transaction the
    /// construct itself begins.
    ///
    /// BC's own bodies, decompiled: <c>NavReport.RunReportInternalCoreAsync</c> calls
    /// <c>Session.BeginTransaction()</c> immediately before <c>GetReportRecords()</c> and
    /// <c>Session.EndTransaction(...)</c> after the data-item iterator;
    /// <c>NavRecord.ValidateFieldsAsync</c> opens one per field around
    /// <c>ValidateAsync</c>; <c>NavForm.ModifyAsync</c> the same around the page's Modify.
    ///
    /// The BC claim is pinned upstream (corpus 60878 Test11 and Test13, PR body's
    /// <c>Corpus-PR:</c> line). This pins the runner's own brackets.
    ///
    /// The negative arm is the one that makes this prove something: <c>K_</c> shows the test
    /// BODY is still refused. Without it every assertion here would also pass if the brackets
    /// simply disabled the no-transaction scope outright.
    /// </summary>
    [SkippableFact]
    public void UnderTransactionModelNone_AReportAndAPageFieldValidateMayWrite()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-writetx-none-surfaces-3543");
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "b3543000-0000-4000-8000-000000003543",
          "name": "WriteTxNoneSurfaces3543",
          "publisher": "Repro3543",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62540, "to": 62549 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(root, "TxnSurfaces.al"), """
        table 62540 "TXS Probe"
        {
            DataClassification = SystemMetadata;

            fields
            {
                field(1; "Entry No."; Integer) { }
                field(2; "Text Field"; Text[100])
                {
                    // Writes a DIFFERENT row from the one the page sits on, so "did the write
                    // land" cannot be satisfied by the page's own Modify of the current record.
                    trigger OnValidate()
                    var
                        Marker: Record "TXS Probe";
                    begin
                        if Rec."Entry No." <> 9414 then begin
                            Marker."Entry No." := 9414;
                            Marker.Insert();
                        end;
                    end;
                }
            }

            keys
            {
                key(PK; "Entry No.") { Clustered = true; }
            }
        }

        report 62541 "TXS Report Inserter"
        {
            ProcessingOnly = true;
            UseRequestPage = false;

            dataset
            {
                dataitem(Loop; Integer)
                {
                    DataItemTableView = sorting(Number) where(Number = const(1));

                    trigger OnAfterGetRecord()
                    var
                        Probe: Record "TXS Probe";
                    begin
                        Probe."Entry No." := 9412;
                        Probe.Insert();
                    end;
                }
            }
        }

        page 62543 "TXS Card"
        {
            PageType = Card;
            SourceTable = "TXS Probe";
            ApplicationArea = All;

            layout
            {
                area(Content)
                {
                    group(General)
                    {
                        field("Entry No."; Rec."Entry No.") { ApplicationArea = All; }
                        field("Text Field"; Rec."Text Field") { ApplicationArea = All; }
                    }
                }
            }
        }

        codeunit 62544 "TXS Tests"
        {
            Subtype = Test;
            TestPermissions = Disabled;

            // Declaration order IS the fixture.

            local procedure MarkerCount(EntryNo: Integer): Integer
            var
                Probe: Record "TXS Probe";
            begin
                Probe.Reset();
                Probe.SetRange("Entry No.", EntryNo);
                exit(Probe.Count());
            end;

            [Test]
            [TransactionModel(TransactionModel::None)]
            procedure A_ReportMayWriteUnderNone()
            var
                Rep: Report "TXS Report Inserter";
            begin
                if MarkerCount(9412) <> 0 then
                    Error('TXS1 FAIL: the report marker must not exist before the report runs');

                Rep.UseRequestPage(false);
                Rep.RunModal();

                if MarkerCount(9412) <> 1 then
                    Error('TXS1 FAIL: a report run from a None test must be able to write; got %1 row(s)', MarkerCount(9412));
                if Database.IsInWriteTransaction() then
                    Error('TXS1 FAIL: the transaction the report began must end with the report');
            end;

            // The page arm needs a row to open on, and a None body cannot write one for itself
            // (#3480). A default-model test writes it; the platform commits it at this boundary.
            [Test]
            procedure B_SeedsTheRowThePageOpensOn()
            var
                Probe: Record "TXS Probe";
            begin
                Probe."Entry No." := 9415;
                Probe.Insert();

                if not Database.IsInWriteTransaction() then
                    Error('TXS2 FAIL: an uncommitted Insert must open a write transaction inside the test that made it');
            end;

            [Test]
            [TransactionModel(TransactionModel::None)]
            procedure C_PageFieldValidateMayWriteUnderNone()
            var
                Probe: Record "TXS Probe";
                Card: TestPage "TXS Card";
            begin
                if MarkerCount(9414) <> 0 then
                    Error('TXS3 FAIL: the OnValidate marker must not exist before the edit');
                if not Probe.Get(9415) then
                    Error('TXS3 FAIL: the previous default-model test''s seed row must be visible here');

                Card.OpenEdit();
                Card.GoToKey(9415);
                Card."Text Field".SetValue('EDITED-UNDER-NONE');
                Card.Close();

                if MarkerCount(9414) <> 1 then
                    Error('TXS3 FAIL: a page field''s OnValidate driven from a None test must be able to write; got %1 row(s)', MarkerCount(9414));
                if Database.IsInWriteTransaction() then
                    Error('TXS3 FAIL: the transaction the page began must end with the page');
            end;

            // The negative arm, and the reason the three above prove anything: the brackets must
            // make a write legal INSIDE those constructs WITHOUT reopening the test body itself.
            // Delete either bracket's Exit and this test starts passing writes BC refuses.
            [Test]
            [TransactionModel(TransactionModel::None)]
            procedure D_TheTestBodyItselfIsStillRefused()
            var
                Probe: Record "TXS Probe";
            begin
                Probe."Entry No." := 9416;
                asserterror Probe.Insert();

                if GetLastErrorText() <> 'A transaction must be started before changes can be made to the database.' then
                    Error('TXS4 FAIL: expected BC''s no-transaction refusal, got [%1]', GetLastErrorText());
                if MarkerCount(9416) <> 0 then
                    Error('TXS4 FAIL: a refused write must not have written a row');
            end;

            // And the scope must not outlive the None tests that opened it.
            [Test]
            procedure E_ADefaultModelTestAfterwardsCanStillWrite()
            var
                Probe: Record "TXS Probe";
            begin
                Probe."Entry No." := 9417;
                Probe.Insert();

                if not Database.IsInWriteTransaction() then
                    Error('TXS5 FAIL: a default-model test after the None tests must still be able to write');
            end;
        }
        """);

        var (output, exitCode) = RunRunner(root);

        Assert.True(exitCode == 0,
            $"Expected all five tests to pass (exit 0); got exit {exitCode}.\n{output}");
        Assert.DoesNotContain("FAIL", output);
        Assert.Contains("PASS  Codeunit62544.A_ReportMayWriteUnderNone", output);
        Assert.Contains("PASS  Codeunit62544.B_SeedsTheRowThePageOpensOn", output);
        Assert.Contains("PASS  Codeunit62544.C_PageFieldValidateMayWriteUnderNone", output);
        Assert.Contains("PASS  Codeunit62544.D_TheTestBodyItselfIsStillRefused", output);
        Assert.Contains("PASS  Codeunit62544.E_ADefaultModelTestAfterwardsCanStillWrite", output);
    }
}
