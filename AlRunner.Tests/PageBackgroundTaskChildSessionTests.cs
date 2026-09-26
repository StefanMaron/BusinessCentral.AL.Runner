using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Runner-mechanism tests for issue #4679: a page background task worker runs inline in the
/// caller's session here (RunnerPageBackgroundTaskGap), but must not see the caller's write
/// transaction, and a commit inside it must not move the caller's rollback floor.
///
/// The BC claim is adjudicated upstream by corpus codeunit 67202 "Test Page BgTask Tx Tests";
/// this is the same shape in the unit legs, so a regression fails here without a corpus leg.
/// No Library Assert and no <c>"application"</c> (no-base-app-in-csharp-tests.md): each AL test
/// raises its own Error() with the observed value.
/// </summary>
public class PageBackgroundTaskChildSessionTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static (string output, int exit) RunRunner(string bundle)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" --show-pass \"").Append(bundle).Append('"');
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
    public void Worker_RunsOutsideTheCallersWriteTransaction_AndItsCommitIsItsOwn()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-pbt-child-session-4679");
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "b4679000-0000-4000-8000-000000004679",
          "name": "PbtChildSession4679",
          "publisher": "Repro4679",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 64679, "to": 64684 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(root, "Pbt.al"), """
        table 64679 "PCS Row"
        {
            DataClassification = SystemMetadata;
            fields { field(1; "No."; Code[20]) { } }
            keys { key(PK; "No.") { Clustered = true; } }
        }

        codeunit 64680 "PCS Noop"
        {
            trigger OnRun()
            begin
            end;
        }

        codeunit 64681 "PCS Worker"
        {
            trigger OnRun()
            var
                Row: Record "PCS Row";
                Results: Dictionary of [Text, Text];
            begin
                Results.Add('Count', Format(Row.Count()));
                if Database.IsInWriteTransaction() then
                    Results.Add('InWriteTx', 'true')
                else
                    Results.Add('InWriteTx', 'false');
                if Codeunit.Run(Codeunit::"PCS Noop") then
                    Results.Add('GuardedRun', 'true')
                else
                    Results.Add('GuardedRun', 'false');
                Page.SetBackgroundTaskResult(Results);
            end;
        }

        page 64682 "PCS Card"
        {
            PageType = Card;
            SourceTable = "PCS Row";
            layout { area(Content) { field("No."; Rec."No.") { } } }
        }

        codeunit 64683 "PCS Tests"
        {
            Subtype = Test;
            TestPermissions = Disabled;

            local procedure SeedAndRun(SecondNo: Code[20]; var Results: Dictionary of [Text, Text])
            var
                Row: Record "PCS Row";
                Card: TestPage "PCS Card";
                Params: Dictionary of [Text, Text];
            begin
                Row.DeleteAll();
                Row."No." := 'A';
                Row.Insert();
                Row."No." := SecondNo;
                Row.Insert();
                if not Database.IsInWriteTransaction() then
                    Error('PCS precondition: the seeded rows must be uncommitted');
                Card.OpenView();
                Results := Card.RunPageBackgroundTask(Codeunit::"PCS Worker", Params, true);
                Card.Close();
            end;

            [Test]
            procedure WorkerSeesRowsAndNoWriteTransaction()
            var
                Results: Dictionary of [Text, Text];
            begin
                SeedAndRun('B', Results);
                if Results.Get('Count') <> '2' then
                    Error('PCS1 FAIL: worker Count=%1, expected 2', Results.Get('Count'));
                if Results.Get('InWriteTx') <> 'false' then
                    Error('PCS1 FAIL: worker InWriteTx=%1, expected false', Results.Get('InWriteTx'));
                if Results.Get('GuardedRun') <> 'true' then
                    Error('PCS1 FAIL: worker GuardedRun=%1, expected true', Results.Get('GuardedRun'));
            end;

            [Test]
            procedure CallerStillInWriteTransactionAfterTask()
            var
                Results: Dictionary of [Text, Text];
                Ok: Boolean;
            begin
                SeedAndRun('B', Results);
                if not Database.IsInWriteTransaction() then
                    Error('PCS2 FAIL: the caller''s write transaction must survive the task');
                asserterror Ok := Codeunit.Run(Codeunit::"PCS Noop");
                if StrPos(GetLastErrorText(), 'the transaction is stopped') = 0 then
                    Error('PCS2 FAIL: guarded run in the caller must be refused, got: %1', GetLastErrorText());
            end;

            [Test]
            procedure WorkerCommitDoesNotCommitCallersRows()
            var
                Row: Record "PCS Row";
                Results: Dictionary of [Text, Text];
            begin
                // 'RB' is written by this test only: an earlier passing test commits its rows at
                // the test boundary, so a shared key would survive the rollback for that reason.
                SeedAndRun('RB', Results);
                asserterror Error('PCS3 probe');
                if Row.Get('RB') then
                    Error('PCS3 FAIL: the worker''s commit made the caller''s uncommitted row durable');
            end;
        }
        """);

        var (output, exitCode) = RunRunner(root);

        Assert.True(exitCode == 0,
            $"Expected all three child-session tests to pass (exit 0); got exit {exitCode}.\n{output}");
        Assert.DoesNotContain("FAIL", output);
        Assert.Contains("PASS  Codeunit64683.WorkerSeesRowsAndNoWriteTransaction", output);
        Assert.Contains("PASS  Codeunit64683.CallerStillInWriteTransactionAfterTask", output);
        Assert.Contains("PASS  Codeunit64683.WorkerCommitDoesNotCommitCallersRows", output);
    }
}
