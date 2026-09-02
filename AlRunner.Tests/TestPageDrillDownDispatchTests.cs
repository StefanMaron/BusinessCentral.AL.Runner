// TestPageDrillDownDispatchTests — issue #1774 (TestPage field DrillDown() never
// dispatched OnDrillDown; #57 left it a literal no-op stub).
//
// This is a RUNNER-MECHANISM test, not a claim about what real BC does: it proves that
// OUR OWN dispatch path — RunnerPageInstance.RaiseOnDrillDown, wired up from
// MockTestPage's LiveNavTestField.Drilldown()/PageVariableTestField.Drilldown() — actually
// routes a TestPage field's DrillDown() call to the page control's own OnDrillDown
// trigger, and that a control with no such trigger raises the fixed platform error real
// BC gives here.
//
// The BEHAVIORAL claim ("DrillDown() runs OnDrillDown on real BC, and raises 'The
// NavDrilldownAction method is not supported.' when no trigger is declared, confirmed on
// BC 27.5 and 28.3") is proven upstream against a live BC service tier — see
// StefanMaron/BusinessCentral.AL.Language.Tests PR #32, codeunit 60948
// "Test Page DrillDown Tests" — per .claude/rules/bc-behavior-tests-go-upstream.md. This
// test exists so a regression in OUR OWN dispatch mechanism fails loudly here, without
// needing the corpus's al-language submodule pin bumped (which, per the orchestrator, is
// deliberately deferred to a separate centralized PR to avoid pulling in other agents'
// still-unfixed corpus gaps).
//
// RED/GREEN proof: temporarily reverting RunnerPageInstance.RaiseOnDrillDown /
// MockTestPage.Drilldown() back to the pre-fix no-op (`public void Drilldown() { }` on
// both LiveNavTestField and PageVariableTestField, and no RaiseOnDrillDown at all) makes
// both tests below fail: the positive test's marker row is never written (Error "OnDrillDown
// trigger did not run"), and the negative test's asserterror never fires (Error "expected
// DrillDown() on a trigger-less control to fail").

using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

// Spawns the runner as a subprocess, same convention as BatchAppIdentityTests. Used to be
// [Collection("server-serial")] to avoid concurrent `dotnet run`s and no longer is — #1809.
// This test's own flake under box contention (seen while developing #1808) was traced to
// generic shared-box starvation, not a timing assumption in the test itself: RunBundled()
// already waits up to 600s for the subprocess and the fixture uses a per-instance Guid temp
// dir, so there is no fixed-timeout or shared-path race here to fix.
public sealed class TestPageDrillDownDispatchTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public TestPageDrillDownDispatchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-drilldown-dispatch", Guid.NewGuid().ToString("N"));
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
    /// Two controls bound to DIFFERENT source fields on the same repeater row: one whose
    /// OnDrillDown trigger writes a marker row naming the current row (the observable side
    /// effect a fix must produce), one with no OnDrillDown trigger at all (the exact
    /// platform error a fix must reproduce).
    /// </summary>
    private void WriteBundle()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "app.json"), """
        {
          "id": "b7c4e2a1-3d5f-4890-9a1b-6c7d8e9f0a1b",
          "name": "Runner Mechanism - TestPage DrillDown Dispatch",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62380, "to": 62389 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(_root, "DDMechRow.Table.al"), """
        table 62380 "DD Mech Row"
        {
            DataClassification = CustomerContent;

            fields
            {
                field(1; "No."; Code[20]) { }
                field(2; Val; Text[50]) { }
            }

            keys
            {
                key(PK; "No.") { Clustered = true; }
            }
        }
        """);

        File.WriteAllText(Path.Combine(_root, "DDMechList.Page.al"), """
        page 62381 "DD Mech List"
        {
            PageType = List;
            SourceTable = "DD Mech Row";
            ApplicationArea = All;
            UsageCategory = Lists;

            layout
            {
                area(Content)
                {
                    repeater(Rows)
                    {
                        field("No."; Rec."No.")
                        {
                            ApplicationArea = All;
                        }

                        field(WithTrigger; Rec.Val)
                        {
                            ApplicationArea = All;
                            DrillDown = true;

                            trigger OnDrillDown()
                            var
                                Marker: Record "DD Mech Row";
                            begin
                                if not Marker.Get('FIRED') then begin
                                    Marker.Init();
                                    Marker."No." := 'FIRED';
                                    Marker.Val := Rec."No.";
                                    Marker.Insert();
                                end else begin
                                    Marker.Val := Rec."No.";
                                    Marker.Modify();
                                end;
                            end;
                        }

                        field(NoTrigger; Rec."No.")
                        {
                            ApplicationArea = All;
                        }
                    }
                }
            }
        }
        """);

        File.WriteAllText(Path.Combine(_root, "DDMechTests.Codeunit.al"), """
        codeunit 62382 "DD Mech Tests"
        {
            Subtype = Test;

            [Test]
            procedure DrillDownRunsOnDrillDownTriggerAgainstCurrentRow()
            var
                Row: Record "DD Mech Row";
                Marker: Record "DD Mech Row";
                DDList: TestPage "DD Mech List";
            begin
                Row.DeleteAll();

                Row.Init();
                Row."No." := 'ROW1';
                Row.Insert();

                DDList.OpenView();
                DDList.First();
                DDList.WithTrigger.DrillDown();
                DDList.Close();

                if not Marker.Get('FIRED') then
                    Error('DrillDown() must have run the control''s OnDrillDown trigger');
                if Marker.Val <> 'ROW1' then
                    Error('OnDrillDown trigger must have seen the page''s current row, got %1', Marker.Val);
            end;

            [Test]
            procedure DrillDownWithNoTriggerRaisesTheFixedPlatformError()
            var
                Row: Record "DD Mech Row";
                DDList: TestPage "DD Mech List";
            begin
                Row.DeleteAll();

                Row.Init();
                Row."No." := 'ROW2';
                Row.Insert();

                DDList.OpenView();
                DDList.First();
                asserterror DDList.NoTrigger.DrillDown();
                if StrPos(GetLastErrorText(), 'The NavDrilldownAction method is not supported.') = 0 then
                    Error('Expected the fixed platform error, got: %1', GetLastErrorText());
                DDList.Close();
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
    /// Positive + negative in one run: the trigger-bearing control's DrillDown() must run
    /// its OnDrillDown against the page's current row (a concrete value, not merely "did
    /// not throw"), and the trigger-less control's DrillDown() must raise the exact BC
    /// platform error rather than silently doing nothing.
    /// </summary>
    [SkippableFact]
    public void DrillDown_DispatchesTriggerAndRefusesWhenAbsent()
    {
        TestArtifacts.SkipIfMissing();

        WriteBundle();
        var (output, exit) = RunBundled();

        Assert.True(exit == 0, $"Expected the bundle to pass; exit={exit}\n{output}");
        Assert.Contains("PASS  Codeunit62382.DrillDownRunsOnDrillDownTriggerAgainstCurrentRow", output);
        Assert.Contains("PASS  Codeunit62382.DrillDownWithNoTriggerRaisesTheFixedPlatformError", output);
        Assert.DoesNotContain("FAIL", output);
    }
}
