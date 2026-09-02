// SuiteAbortOnTimeoutTests — RED->GREEN guard for issue #2415.
//
// When one AL test hangs and its per-test watchdog fires, TestExecutor.Run used to
// respond with a bare `return results;` from deep inside the codeunit/method loop —
// ending the WHOLE Run() call for the rest of THIS codeunit and every LATER codeunit
// in the same app group, silently. Because Run() returned normally (no exception),
// Program.cs's only source of "suite errors" (the catch around executor.Run) never
// fired either, so a run that dropped real tests still printed "0 suite errors" and
// could exit clean. Measured for real: 730 tests ran vs. 834 on a full corpus leg,
// 104 missing, nothing in the output saying so.
//
// This fixture is deliberately NOT placed under tests/runner-extras/: that directory
// is swept in ONE process by CI with `--strict --count-baseline`, so a bundle that
// intentionally times out and exits non-zero would permanently fail every OTHER
// suite in that sweep. TestTimeoutFlagTests.cs established the isolated-subprocess
// pattern for exactly this class of test (per-test-watchdog verification); this
// follows it.
//
// Ghost-test trap avoided: the fixture's codeunit declares three [Test] procedures in
// source order — Hangs, NeverRuns1, NeverRuns2. A no-op fix (the silent early return
// left in place, or a fix that merely changes the return value's shape without
// surfacing anything) makes the assertions below fail because the output carries
// neither the codeunit name, nor the count "2", nor a non-zero exit code.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class SuiteAbortOnTimeoutTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public SuiteAbortOnTimeoutTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-suite-abort-on-timeout", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        WriteFixture(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// Writes a minimal AL package: app.json (no dependencies) and a test codeunit
    /// with THREE [Test] procedures in source order — the first loops forever (the
    /// ONLY way it ever "finishes" is the runner's per-test timeout firing), the
    /// other two are trivial no-ops that must never actually run.
    /// </summary>
    private static void WriteFixture(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "c3d4e5f6-a7b8-9012-3456-7890abcdef12",
          "name": "Suite Abort On Timeout Test Fixture",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62210, "to": 62219 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(dir, "SuiteAbort.Codeunit.al"), """
        codeunit 62211 "Suite Abort On Timeout Tests"
        {
            Subtype = Test;

            [Test]
            procedure Hangs()
            begin
                while true do;
            end;

            [Test]
            procedure NeverRuns1()
            begin
            end;

            [Test]
            procedure NeverRuns2()
            begin
            end;
        }
        """);
    }

    private (string output, int exit) RunRunner(params string[] extraArgs)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{_root}\"");
        foreach (var a in extraArgs) args.Append($" {a}");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived  += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        // The fixture's first [Test] never returns on its own; give the runner
        // subprocess enough headroom above the (short) --test-timeout to finish the
        // whole run, but well under a genuine hang.
        if (!p.WaitForExit(120_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    /// <summary>
    /// Positive: a hang must be reported loudly. The codeunit name and the exact
    /// count of [Test] methods it never got to (2 — NeverRuns1 and NeverRuns2) must
    /// both appear in the output, alongside a non-zero "suite errors" tally.
    /// </summary>
    [SkippableFact]
    public void HungTest_AbortsCodeunit_ReportsCodeunitAndSkippedCount()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner("--test-timeout 2");

        Assert.NotEqual(0, exit);
        Assert.Contains("Suite Abort On Timeout Tests", output);
        Assert.Contains("SUITE ABORTED", output);
        // The exact denominator: 2 further [Test] methods (NeverRuns1, NeverRuns2)
        // never ran. A fix that reports SOME count but not this one would still be
        // lying about the run's true shape.
        Assert.Contains("2 further [Test] method(s)", output);
        // "0 suite errors" was the bug's own signature line — must never appear
        // alongside a run that dropped tests.
        Assert.DoesNotContain("0 suite errors", output);
    }

    /// <summary>
    /// Negative: NeverRuns1/NeverRuns2 must not silently vanish from a passing count
    /// either — the summary line's test total must still include the hung test
    /// itself (reported as an Error, not dropped), proving the abort is additive
    /// reporting on top of the existing per-test outcome, not a replacement for it.
    /// </summary>
    [SkippableFact]
    public void HungTest_StillCountsTowardsTheRunsTestTotal()
    {
        TestArtifacts.SkipIfMissing();

        var (output, _) = RunRunner("--test-timeout 2");

        Assert.Contains("Hangs", output);
        Assert.Contains("Test exceeded 2s timeout.", output);
    }
}
