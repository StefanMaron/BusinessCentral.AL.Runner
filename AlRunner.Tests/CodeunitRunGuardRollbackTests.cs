using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Runner-mechanism test for issue #2334: a failed guarded <c>Codeunit.Run</c> neither
/// trapped (instance form) nor rolled back its own writes.
///
/// Two independent defects, both fixed here:
///
/// 1. <c>CodeunitPatches.NavCodeunit_DoRunAsync</c> (the instance form,
///    <c>SomeCodeunitVar.Run(...)</c>) had no <c>catch when (guarded)</c> — only the
///    static form (<c>Codeunit.Run(Codeunit::X)</c>, via
///    <c>CodeunitPatches.NavCodeunit_RunCodeunit</c>) trapped the inner error and
///    returned <c>false</c>. The two spellings of the same AL construct behaved
///    differently.
///
/// 2. <c>ALDatabasePatches.EndGuardedRunTransaction(commit: false)</c> was a documented
///    no-op — a failed guarded run's own writes were never rolled back. Fixed by giving
///    <c>RecordPatches.TransactionSnapshot</c> a scope-relative snapshot
///    (<c>PushTransactionWorldScope</c> / <c>PopTransactionWorldScope</c>), independent
///    of the top-level "since the last real commit" one, so a scope's own writes can be
///    undone without discarding whatever the caller left uncommitted before entering it.
///
/// The BEHAVIORAL claim ("both spellings of a guarded Codeunit.Run trap the same way and
/// roll their own writes back on failure") is a plain BC-behaviour claim and belongs
/// upstream — see the StefanMaron/BusinessCentral.AL.Language.Tests PR extending
/// TestCodeunitRunGuard.al (Codeunit 60217), per
/// .claude/rules/bc-behavior-tests-go-upstream.md. This test pins the RUNNER's OWN
/// mechanism so a regression fails loudly here, without depending on the submodule pin
/// having moved yet.
///
/// No Library Assert dependency (no "application" in the fixture's app.json — see
/// .claude/rules/no-base-app-in-csharp-tests.md): each test raises its own Error() with
/// the observed value, so the runner's own PASS/FAIL output is the assertion surface.
/// </summary>
public class CodeunitRunGuardRollbackTests
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
    public void GuardedRun_InstanceForm_TrapsAndRollsBack_BothSpellingsAgree()
    {
        TestArtifacts.SkipIfMissing();

        var root = Path.Combine(Path.GetTempPath(), "al-runner-codeunit-run-guard-2334", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "b2334000-0000-4000-8000-000000002334",
          "name": "CodeunitRunGuard2334",
          "publisher": "Repro2334",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62334, "to": 62339 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(root, "CrgProbe.al"), """
        table 62334 "CRG Probe"
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

        codeunit 62335 "CRG Erroring"
        {
            trigger OnRun()
            begin
                Error('CRG-BOOM');
            end;
        }

        codeunit 62336 "CRG Write Then Error"
        {
            trigger OnRun()
            var
                Probe: Record "CRG Probe";
            begin
                Probe.Init();
                Probe."Entry No." := 1;
                Probe.Insert();
                Error('CRG-WRITE-BOOM');
            end;
        }

        codeunit 62337 "CRG Tests"
        {
            Subtype = Test;
            TestPermissions = Disabled;

            local procedure Initialize()
            var
                Probe: Record "CRG Probe";
            begin
                Probe.DeleteAll();
                Commit();
            end;

            // Half 1: the STATIC form already trapped before this fix (regression guard).
            [Test]
            procedure StaticForm_TrapsInnerError_ReturnsFalse()
            var
                Ok: Boolean;
            begin
                Initialize();
                Ok := Codeunit.Run(Codeunit::"CRG Erroring");
                if Ok then
                    Error('CRG1 FAIL: static-form guarded Codeunit.Run must return false on an erroring OnRun.');
            end;

            // Half 1: the INSTANCE form must trap identically — this is the exact defect
            // #2334 reports (only the static form trapped; the instance form rethrew).
            [Test]
            procedure InstanceForm_TrapsInnerError_ReturnsFalse()
            var
                RunGuard: Codeunit "CRG Erroring";
                Ok: Boolean;
            begin
                Initialize();
                Ok := RunGuard.Run();
                if Ok then
                    Error('CRG2 FAIL: instance-form guarded Run() must return false on an erroring OnRun.');
            end;

            // Half 2: a guarded run's own writes must not survive when it fails, for BOTH
            // spellings — EndGuardedRunTransaction(commit: false) used to be a no-op.
            [Test]
            procedure StaticForm_WriteThenError_RollsBackInsertedRow()
            var
                Probe: Record "CRG Probe";
                Ok: Boolean;
            begin
                Initialize();
                Ok := Codeunit.Run(Codeunit::"CRG Write Then Error");
                if Ok then
                    Error('CRG3 FAIL: static-form guarded Codeunit.Run must return false.');
                if Probe.Count() <> 0 then
                    Error('CRG3 FAIL: expected Count()=0 after a failed static-form guarded run''s own Insert, got %1', Probe.Count());
            end;

            [Test]
            procedure InstanceForm_WriteThenError_RollsBackInsertedRow()
            var
                Probe: Record "CRG Probe";
                RunGuard: Codeunit "CRG Write Then Error";
                Ok: Boolean;
            begin
                Initialize();
                Ok := RunGuard.Run();
                if Ok then
                    Error('CRG4 FAIL: instance-form guarded Run() must return false.');
                if Probe.Count() <> 0 then
                    Error('CRG4 FAIL: expected Count()=0 after a failed instance-form guarded run''s own Insert, got %1', Probe.Count());
            end;

            // Must-not-regress control: a write made by the CALLER before a guarded run
            // that itself writes-then-fails must survive — the scope-relative rollback
            // must undo only the failed scope's OWN writes, not everything since the last
            // commit point (that would be the top-level RollbackToCommitPoint behaviour,
            // which this mechanism is deliberately NOT reusing here).
            [Test]
            procedure CallerUncommittedWrite_SurvivesInnerGuardedRunFailure()
            var
                Probe: Record "CRG Probe";
                Ok: Boolean;
            begin
                Initialize();
                Probe.Init();
                Probe."Entry No." := 9;
                Probe.Insert();
                Commit();

                Ok := Codeunit.Run(Codeunit::"CRG Write Then Error");
                if Ok then
                    Error('CRG5 FAIL: static-form guarded Codeunit.Run must return false.');

                Probe.Reset();
                if Probe.Count() <> 1 then
                    Error('CRG5 FAIL: expected the caller''s own committed row to survive the inner run''s rollback, got Count()=%1', Probe.Count());
            end;
        }
        """);

        var (output, exitCode) = RunRunner(root);

        Assert.True(exitCode == 0,
            $"Expected all five guarded-Codeunit.Run tests to pass (exit 0); got exit {exitCode}.\n{output}");
        Assert.DoesNotContain("FAIL", output);
        Assert.Contains("PASS  Codeunit62337.StaticForm_TrapsInnerError_ReturnsFalse", output);
        Assert.Contains("PASS  Codeunit62337.InstanceForm_TrapsInnerError_ReturnsFalse", output);
        Assert.Contains("PASS  Codeunit62337.StaticForm_WriteThenError_RollsBackInsertedRow", output);
        Assert.Contains("PASS  Codeunit62337.InstanceForm_WriteThenError_RollsBackInsertedRow", output);
        Assert.Contains("PASS  Codeunit62337.CallerUncommittedWrite_SurvivesInnerGuardedRunFailure", output);
    }
}
