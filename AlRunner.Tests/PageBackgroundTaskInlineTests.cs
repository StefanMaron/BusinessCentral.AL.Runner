using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Runner-mechanism test for issue #2514: CurrPage.EnqueueBackgroundTask (from a page trigger)
/// and TestPage.RunPageBackgroundTask both used to crash under a TestPage
/// (NavChildSessionTaskRuntime&lt;T&gt;.RunAsync tried to bootstrap a real child NavSession —
/// Open()/OpenCompanyAsync() — the runner's in-process skeleton cannot faithfully answer).
///
/// AlRunner/Patches/RunnerPageBackgroundTaskGap.cs replaces the two dispatch bodies
/// (NavForm.EnqueueBackgroundTask, NavTestPage.ALRunPageBackgroundTask — see NclCecilRewrite.cs)
/// with an inline reimplementation that runs the worker codeunit against the CURRENT session
/// instead of an isolated child one, then drives BC's own (unmodified)
/// PageBackgroundChildSessionTask.AfterRunTaskAsync / AfterRunTaskErrorAsync, which is what
/// actually raises OnPageBackgroundTaskCompleted / OnPageBackgroundTaskError.
///
/// The BEHAVIORAL claim ("page background tasks run synchronously under BC's own test
/// framework, so their completion/error trigger has already fired by the time
/// OpenView()/GoToRecord()/RunPageBackgroundTask() returns") is a plain-BC-behaviour claim and
/// belongs upstream — see StefanMaron/BusinessCentral.AL.Language.Tests codeunit 60793 "Test
/// Page BgTask Tests", per .claude/rules/bc-behavior-tests-go-upstream.md. This test exists so
/// a regression in OUR OWN inline-dispatch mechanism fails loudly here, spawning the real
/// runner against a synthetic bundle, without depending on the submodule pin having moved yet.
///
/// No Library Assert dependency (no "application" in the fixture's app.json — see
/// .claude/rules/no-base-app-in-csharp-tests.md): each test raises its own Error() with the
/// observed value, so the runner's own PASS/FAIL output is the assertion surface.
/// </summary>
public class PageBackgroundTaskInlineTests
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
    public void PageBackgroundTask_EnqueueAndRunInline_MatchBcMeasuredShapes()
    {
        TestArtifacts.SkipIfMissing();

        var root = Path.Combine(Path.GetTempPath(), "al-runner-pbt-inline-2514", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "b2514000-0000-4000-8000-000000002514",
          "name": "PageBackgroundTaskInline2514",
          "publisher": "Repro2514",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62514, "to": 62524 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(root, "PbtProbe.al"), """
        table 62514 "PBTI Row"
        {
            DataClassification = SystemMetadata;

            fields
            {
                field(1; "No."; Code[20]) { }
                field(2; Handle; Boolean) { }
            }

            keys
            {
                key(PK; "No.") { Clustered = true; }
            }
        }

        codeunit 62515 "PBTI Worker"
        {
            trigger OnRun()
            var
                Params: Dictionary of [Text, Text];
                Results: Dictionary of [Text, Text];
                RowNo: Text;
            begin
                Params := Page.GetBackgroundParameters();
                Params.Get('No', RowNo);
                if RowNo.StartsWith('FAIL-') then
                    Error('PBTI Worker deliberately failed for %1', RowNo);
                Results.Add('Count', 'BG:' + RowNo);
                Page.SetBackgroundTaskResult(Results);
            end;
        }

        page 62516 "PBTI Card"
        {
            PageType = Card;
            SourceTable = "PBTI Row";
            ApplicationArea = All;
            UsageCategory = None;

            layout
            {
                area(Content)
                {
                    field("No."; Rec."No.") { ApplicationArea = All; }
                    field(Handle; Rec.Handle) { ApplicationArea = All; }
                    field(CountTextCtl; CountText) { ApplicationArea = All; Caption = 'Count Text'; }
                    field(LastErrorTextCtl; LastErrorText) { ApplicationArea = All; Caption = 'Last Error Text'; }
                }
            }

            trigger OnAfterGetCurrRecord()
            var
                Args: Dictionary of [Text, Text];
            begin
                Clear(Args);
                Args.Add('No', Rec."No.");
                CurrPage.EnqueueBackgroundTask(TaskId, Codeunit::"PBTI Worker", Args, 5000);
            end;

            trigger OnPageBackgroundTaskCompleted(TaskId: Integer; Results: Dictionary of [Text, Text])
            begin
                Results.Get('Count', CountText);
            end;

            trigger OnPageBackgroundTaskError(TaskId: Integer; ErrorCode: Text; ErrorText: Text; ErrorCallStack: Text; var IsHandled: Boolean)
            begin
                LastErrorText := ErrorText;
                IsHandled := Rec.Handle;
            end;

            var
                TaskId: Integer;
                CountText: Text;
                LastErrorText: Text;
        }

        codeunit 62517 "PBTI Tests"
        {
            Subtype = Test;
            TestPermissions = Disabled;

            local procedure Initialize()
            var
                Row: Record "PBTI Row";
            begin
                Row.DeleteAll();
            end;

            local procedure SeedRow(No: Code[20]; Handle: Boolean)
            var
                Row: Record "PBTI Row";
            begin
                Row.Init();
                Row."No." := No;
                Row.Handle := Handle;
                Row.Insert();
            end;

            // Enqueue must complete -- and its completion trigger must have already run -- by
            // the time OpenView() returns, and again by the time GoToRecord() returns.
            [Test]
            procedure EnqueueBackgroundTask_CompletesBeforeOpenAndGoToRecordReturn()
            var
                Row: Record "PBTI Row";
                Card: TestPage "PBTI Card";
            begin
                Initialize();
                SeedRow('BGT-1', false);
                SeedRow('BGT-2', false);
                Row.Get('BGT-2');

                Card.OpenView();
                if Card.CountTextCtl.Value() <> 'BG:BGT-1' then
                    Error('ARM1 FAIL: expected BG:BGT-1 after OpenView, got %1', Card.CountTextCtl.Value());

                if not Card.GoToRecord(Row) then
                    Error('ARM1 FAIL: GoToRecord could not find seeded row BGT-2');
                if Card.CountTextCtl.Value() <> 'BG:BGT-2' then
                    Error('ARM1 FAIL: expected BG:BGT-2 after GoToRecord, got %1', Card.CountTextCtl.Value());
                Card.Close();
            end;

            // TestPage.RunPageBackgroundTask(..., false) must return the worker's own Results
            // dictionary.
            [Test]
            procedure RunPageBackgroundTask_ReturnsWorkerResult()
            var
                Card: TestPage "PBTI Card";
                Params: Dictionary of [Text, Text];
                Results: Dictionary of [Text, Text];
                CountValue: Text;
            begin
                Initialize();
                SeedRow('BGT-1', false);

                Card.OpenView();
                Params.Add('No', 'RPT-1');
                Results := Card.RunPageBackgroundTask(Codeunit::"PBTI Worker", Params, false);
                Card.Close();

                if not Results.Get('Count', CountValue) then
                    Error('ARM2 FAIL: RunPageBackgroundTask did not return a Count result');
                if CountValue <> 'BG:RPT-1' then
                    Error('ARM2 FAIL: expected BG:RPT-1, got %1', CountValue);
            end;

            // A worker Error() reaches OnPageBackgroundTaskError with the worker's own error
            // text; IsHandled := true suppresses the exception.
            [Test]
            procedure EnqueueBackgroundTask_HandledErrorDoesNotPropagate()
            var
                Row: Record "PBTI Row";
                Card: TestPage "PBTI Card";
            begin
                Initialize();
                SeedRow('BGT-1', false);
                SeedRow('FAIL-H', true);
                Row.Get('FAIL-H');

                Card.OpenView();
                if not Card.GoToRecord(Row) then
                    Error('ARM3 FAIL: GoToRecord could not find seeded row FAIL-H even though its background task errors');
                if Card.LastErrorTextCtl.Value() <> 'PBTI Worker deliberately failed for FAIL-H' then
                    Error('ARM3 FAIL: expected the worker''s own error text, got %1', Card.LastErrorTextCtl.Value());
                Card.Close();
            end;

            // The same error, left unhandled (IsHandled stays false) -- must propagate out of
            // GoToRecord rather than be swallowed.
            [Test]
            procedure EnqueueBackgroundTask_UnhandledErrorPropagates()
            var
                Row: Record "PBTI Row";
                Card: TestPage "PBTI Card";
            begin
                Initialize();
                SeedRow('BGT-1', false);
                SeedRow('FAIL-U', false);
                Row.Get('FAIL-U');

                Card.OpenView();
                asserterror Card.GoToRecord(Row);
                if GetLastErrorText() <> 'PBTI Worker deliberately failed for FAIL-U' then
                    Error('ARM4 FAIL: expected the worker''s own error text to propagate, got %1', GetLastErrorText());
                Card.Close();
            end;

            // A worker codeunit's own Insert() must be refused with BC's permission-denied
            // wording (measured verbatim against BC 27.5/28.3, corpus PR #135), and the row
            // must not land -- the write never reaches the table, it is not merely rolled
            // back afterward. See PageBackgroundTaskWritePatches.cs for the full mechanism.
            [Test]
            procedure RunPageBackgroundTask_WorkerInsert_RefusedByReadOnlySession()
            var
                Row: Record "PBTI Row";
                Card: TestPage "PBTI Card";
                Params: Dictionary of [Text, Text];
                Results: Dictionary of [Text, Text];
            begin
                Initialize();
                Card.OpenView();

                Params.Add('No', 'WR-NEW');
                Params.Add('Write', 'true');
                asserterror Results := Card.RunPageBackgroundTask(Codeunit::"PBTI WriteWorker", Params, false);
                if StrPos(GetLastErrorText(), 'Sorry, the current permissions prevented the action') = 0 then
                    Error('ARM5 FAIL: expected BC''s permission-denied wording, got %1', GetLastErrorText());
                if Row.Get('WR-NEW') then
                    Error('ARM5 FAIL: a refused Insert() must not have landed the row');
                Card.Close();
            end;
        }

        codeunit 62518 "PBTI WriteWorker"
        {
            trigger OnRun()
            var
                Row: Record "PBTI Row";
                Params: Dictionary of [Text, Text];
                RowNo: Text;
            begin
                Params := Page.GetBackgroundParameters();
                Params.Get('No', RowNo);
                Row.Init();
                Row."No." := CopyStr(RowNo, 1, MaxStrLen(Row."No."));
                Row.Insert();
            end;
        }
        """);

        var (output, exitCode) = RunRunner(root);

        Assert.True(exitCode == 0,
            $"Expected all five page-background-task tests to pass (exit 0); got exit {exitCode}.\n{output}");
        Assert.DoesNotContain("FAIL", output);
        Assert.Contains("PASS  Codeunit62517.EnqueueBackgroundTask_CompletesBeforeOpenAndGoToRecordReturn", output);
        Assert.Contains("PASS  Codeunit62517.RunPageBackgroundTask_ReturnsWorkerResult", output);
        Assert.Contains("PASS  Codeunit62517.EnqueueBackgroundTask_HandledErrorDoesNotPropagate", output);
        Assert.Contains("PASS  Codeunit62517.EnqueueBackgroundTask_UnhandledErrorPropagates", output);
        Assert.Contains("PASS  Codeunit62517.RunPageBackgroundTask_WorkerInsert_RefusedByReadOnlySession", output);
    }
}
