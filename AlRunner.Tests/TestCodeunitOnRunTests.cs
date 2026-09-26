using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Runner-mechanism test for issue #4694: a test codeunit's own <c>OnRun</c> trigger runs once,
/// on the codeunit's instance, before its first test method — as BC's
/// <c>NavTestCodeunit.DoRunAsync</c> does — and a failing <c>OnRun</c> means none of the
/// codeunit's test methods run.
///
/// The plain-BC half (a global and a row set in OnRun are visible to the tests) is pinned upstream
/// in the corpus (PR body's <c>Corpus-PR:</c> line). This pins what only the runner decides: that
/// it holds under each <c>--isolation</c> mode and under <c>--test</c>, and how a failing OnRun is
/// reported. See docs/limitations.md#test-codeunit-onrun.
///
/// No Base Application dependency (.claude/rules/no-base-app-in-csharp-tests.md): each AL test
/// raises its own Error() carrying the observed state, and the runner's PASS/FAIL output is the
/// assertion surface.
/// </summary>
public class TestCodeunitOnRunTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static (string output, int exit) RunRunner(string bundle, params string[] extra)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        foreach (var e in extra) args.Append(" \"").Append(e).Append('"');
        args.Append(" \"").Append(bundle).Append('"');
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

    private static string WriteBundle(string name)
    {
        var root = TestScratch.Dir(name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "b4694000-0000-4000-8000-000000004694",
          "name": "TestCodeunitOnRun4694",
          "publisher": "Repro4694",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62690, "to": 62699 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(root, "OnRunProbe.al"), """
        table 62690 "TOR Probe"
        {
            DataClassification = SystemMetadata;
            fields { field(1; "Entry No."; Integer) { } }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }

        // SingleInstance, so a count survives a fresh test-codeunit instance: it counts how often
        // OnRun ran, whatever instance it ran on.
        codeunit 62693 "TOR Counter"
        {
            SingleInstance = true;
            var
                Calls: Integer;
            procedure Bump() begin Calls += 1; end;
            procedure Count(): Integer begin exit(Calls); end;
        }

        codeunit 62691 "TOR Tests"
        {
            Subtype = Test;
            TestPermissions = Disabled;

            var
                G: Integer;
                OnRunCallsOnThisInstance: Integer;

            // G += 42 rather than G := 42, so running OnRun twice on one instance reads 84.
            trigger OnRun()
            var
                Probe: Record "TOR Probe";
                Counter: Codeunit "TOR Counter";
            begin
                G += 42;
                OnRunCallsOnThisInstance += 1;
                Counter.Bump();
                Probe."Entry No." := 7;
                Probe.Insert();
            end;

            [Test]
            procedure A_GlobalSetByOnRunIsVisible()
            begin
                if G <> 42 then
                    Error('TOR1 FAIL: G=%1', G);
            end;

            [Test]
            procedure B_RowInsertedByOnRunIsVisible()
            var
                Probe: Record "TOR Probe";
            begin
                if not Probe.Get(7) then
                    Error('TOR2 FAIL: row 7 missing, count=%1', Probe.Count());
                // A row this test writes: under --isolation test the next test must not see it,
                // under the other modes it must.
                Probe."Entry No." := 8;
                Probe.Insert();
            end;

            [Test]
            procedure C_OnRunCallCount()
            var
                Probe: Record "TOR Probe";
                Counter: Codeunit "TOR Counter";
            begin
                if OnRunCallsOnThisInstance <> 1 then
                    Error('TOR3 FAIL: OnRun ran %1 time(s) on this instance', OnRunCallsOnThisInstance);
                Error('TOR3 REPORT: counter=%1 row8=%2', Counter.Count(), Probe.Get(8));
            end;
        }

        // A test codeunit declaring no OnRun, run after the one above: its rows are gone at the
        // codeunit boundary under the default isolation.
        codeunit 62694 "TOR No OnRun Tests"
        {
            Subtype = Test;
            TestPermissions = Disabled;

            [Test]
            procedure D_PreviousCodeunitsOnRunRowIsGone()
            var
                Probe: Record "TOR Probe";
            begin
                if Probe.Get(7) then
                    Error('TOR4 FAIL: row 7 from the previous codeunit''s OnRun leaked past the codeunit boundary');
            end;
        }

        codeunit 62695 "TOR Failing OnRun Tests"
        {
            Subtype = Test;
            TestPermissions = Disabled;

            trigger OnRun()
            var
                Probe: Record "TOR Probe";
            begin
                Probe."Entry No." := 9;
                Probe.Insert();
                Error('TOR5 OnRun boom');
            end;

            [Test]
            procedure E_WouldPass()
            begin
            end;

            [Test]
            procedure F_WouldAlsoPass()
            begin
            end;
        }

        codeunit 62696 "TOR After Failing OnRun"
        {
            Subtype = Test;
            TestPermissions = Disabled;

            [Test]
            procedure G_FailedOnRunsWriteWasRolledBack()
            var
                Probe: Record "TOR Probe";
            begin
                if Probe.Get(9) then
                    Error('TOR6 FAIL: the failed OnRun''s write survived');
            end;
        }
        """);
        return root;
    }

    private static void Has(string output, string s) =>
        Assert.True(output.Contains(s), $"expected [{s}] in runner output:\n{output}");

    private static void Lacks(string output, string s) =>
        Assert.False(output.Contains(s), $"did not expect [{s}] in runner output:\n{output}");

    private static void AssertFailingOnRunReported(string output)
    {
        foreach (var method in new[] { "E_WouldPass", "F_WouldAlsoPass" })
        {
            var line = output.Split('\n').FirstOrDefault(l => l.Contains($"Codeunit62695.{method}"));
            Assert.True(line != null, $"no result line for {method}.\n{output}");
            Assert.DoesNotContain("PASS", line);
        }
        Has(output, "OnRun trigger failed, so none of its test methods ran");
        Has(output, "TOR5 OnRun boom");
        Has(output, "PASS  Codeunit62696.G_FailedOnRunsWriteWasRolledBack");
    }

    [SkippableFact]
    public void DefaultIsolation_OnRunRunsOnceBeforeTheTests_AndAFailingOnRunRunsNoTest()
    {
        TestArtifacts.SkipIfMissing();
        var (output, _) = RunRunner(WriteBundle("al-runner-testcu-onrun-4694-codeunit"));

        Has(output, "PASS  Codeunit62691.A_GlobalSetByOnRunIsVisible");
        Has(output, "PASS  Codeunit62691.B_RowInsertedByOnRunIsVisible");
        // Once per codeunit, and B_'s row is still there for C_ (one transaction per codeunit).
        Has(output, "TOR3 REPORT: counter=1 row8=Yes");
        Has(output, "PASS  Codeunit62694.D_PreviousCodeunitsOnRunRowIsGone");
        Lacks(output, "TOR1 FAIL");
        Lacks(output, "TOR3 FAIL");
        AssertFailingOnRunReported(output);
    }

    [SkippableFact]
    public void TestFilter_SelectingOneTest_StillRunsOnRunFirst()
    {
        TestArtifacts.SkipIfMissing();
        var (output, _) = RunRunner(WriteBundle("al-runner-testcu-onrun-4694-filter"),
            "--test", "C_OnRunCallCount");

        Has(output, "TOR3 REPORT: counter=1 row8=No");
        Lacks(output, "TOR3 FAIL");
        Lacks(output, "Codeunit62691.A_");
    }

    [SkippableFact]
    public void TestIsolation_EveryTestStartsFromThePostOnRunState()
    {
        TestArtifacts.SkipIfMissing();
        var (output, _) = RunRunner(WriteBundle("al-runner-testcu-onrun-4694-test"),
            "--isolation", "test");

        Has(output, "PASS  Codeunit62691.A_GlobalSetByOnRunIsVisible");
        Has(output, "PASS  Codeunit62691.B_RowInsertedByOnRunIsVisible");
        // A fresh instance and a fresh database per test, each starting after its own OnRun:
        // OnRun ran once on C_'s fresh instance (the SingleInstance counter is reset per test in
        // this mode too), and B_'s row 8 is gone.
        Has(output, "TOR3 REPORT: counter=1 row8=No");
        Lacks(output, "TOR3 FAIL");
        AssertFailingOnRunReported(output);
    }

    [SkippableFact]
    public void DisabledIsolation_OnRunRunsOncePerCodeunit()
    {
        TestArtifacts.SkipIfMissing();
        var (output, _) = RunRunner(WriteBundle("al-runner-testcu-onrun-4694-disabled"),
            "--isolation", "disabled");

        Has(output, "PASS  Codeunit62691.A_GlobalSetByOnRunIsVisible");
        Has(output, "PASS  Codeunit62691.B_RowInsertedByOnRunIsVisible");
        Has(output, "TOR3 REPORT: counter=1 row8=Yes");
        Lacks(output, "TOR3 FAIL");
        Has(output, "TOR4 FAIL");   // no reset at all under Disabled
        // ...so only here does a failed OnRun's own write reach the next codeunit unless it is
        // rolled back.
        AssertFailingOnRunReported(output);
    }
}
