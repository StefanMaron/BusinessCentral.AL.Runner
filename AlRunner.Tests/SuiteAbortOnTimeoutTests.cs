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
//
// #2716 extends this file with a RESUME fixture (two codeunits, the first hangs): the JUnit a
// resumed run writes must be the whole run — the earlier attempt's cases as well as the final
// attempt's — because under --jobs the parent aggregates ONLY that XML. Measured on the full
// BaseApp surface at --jobs 12, the aggregate was missing 26% of the tests the shards had run.
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class SuiteAbortOnTimeoutTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;
    private readonly string _resumeRoot;

    public SuiteAbortOnTimeoutTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-suite-abort-on-timeout", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        WriteFixture(_root);
        _resumeRoot = Path.Combine(Path.GetTempPath(), "al-runner-suite-abort-resume", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_resumeRoot);
        WriteResumeFixture(_resumeRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_resumeRoot, recursive: true); } catch { }
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

    /// <summary>
    /// The shape a watchdog RESUME needs (#2280): two codeunits, the FIRST hangs part-way, so
    /// the abort abandons a later codeunit and AbortResumePlan judges a retry worthwhile.
    ///   attempt 1 runs First:  RanBeforeHang passes, Hangs errors, AbandonedInFirst never runs.
    ///   attempt 2 runs Second: SecondA and SecondB pass (First is excluded as attempted+hung).
    /// The whole run is therefore 4 tests / 1 error; each attempt on its own is 2.
    /// </summary>
    private static void WriteResumeFixture(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "d4e5f6a7-b8c9-0123-4567-890abcdef123",
          "name": "Suite Abort Resume Test Fixture",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62210, "to": 62219 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(dir, "First.Codeunit.al"), """
        codeunit 62212 "Suite Abort Resume First"
        {
            Subtype = Test;

            [Test]
            procedure RanBeforeHang()
            begin
            end;

            [Test]
            procedure Hangs()
            begin
                while true do;
            end;

            [Test]
            procedure AbandonedInFirst()
            begin
            end;
        }
        """);

        File.WriteAllText(Path.Combine(dir, "Second.Codeunit.al"), """
        codeunit 62213 "Suite Abort Resume Second"
        {
            Subtype = Test;

            [Test]
            procedure SecondA()
            begin
            end;

            [Test]
            procedure SecondB()
            begin
            end;
        }
        """);
    }

    private (string output, int exit) RunRunner(params string[] extraArgs)
        => RunRunner(_root, 120_000, extraArgs);

    private (string output, int exit) RunRunner(string bundle, int waitMs, params string[] extraArgs)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{bundle}\"");
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
        if (!p.WaitForExit(waitMs)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
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

    private static List<(string ClassName, string Name)> TestCases(string junitPath)
        => XDocument.Load(junitPath).Descendants("testcase")
            .Select(tc => ((string?)tc.Attribute("classname") ?? "", (string?)tc.Attribute("name") ?? ""))
            .ToList();

    /// <summary>
    /// #2716 positive: after a watchdog resume, --output-junit must hold the WHOLE run — the
    /// earlier attempt's cases (RanBeforeHang, Hangs) as well as the final attempt's (SecondA,
    /// SecondB). The worker's own printed summary already said "4 total (carried: 2)"; the XML
    /// said 2, and under --jobs the parent reads only the XML, so the aggregate lost the 2.
    /// </summary>
    [SkippableFact]
    public void ResumedRun_JUnitContainsEarlierAttemptsCases()
    {
        TestArtifacts.SkipIfMissing();
        var junit = Path.Combine(_resumeRoot, "out", "resumed.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(junit)!);

        // Two attempts, each a full BC boot: generous wait, still far below a real hang.
        var (output, exit) = RunRunner(_resumeRoot, 300_000,
            "--test-timeout 2", "--resume-aborts 1", $"--output-junit \"{junit}\"");

        Assert.NotEqual(0, exit);
        // Sanity: the resume really happened, and the printed summary already carried attempt 1.
        Assert.Contains("resume: a watchdog abort ended this attempt early", output);
        Assert.Contains("carried from earlier attempt(s): 2 tests", output);
        Assert.True(File.Exists(junit), $"JUnit not written: {junit}\n{output}");

        var cases = TestCases(junit);
        var names = cases.Select(c => c.Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "Hangs", "RanBeforeHang", "SecondA", "SecondB" }, names);
        // Once each: the carried suites and the final attempt's suites are disjoint by
        // construction (the resume excludes every attempted codeunit), and the XML must reflect
        // that rather than, say, the whole earlier file being appended twice down a chain.
        Assert.Equal(cases.Count, cases.Distinct().Count());
        // A test inside the HUNG codeunit that never ran is not invented into the record.
        Assert.DoesNotContain("AbandonedInFirst", names);

        // The parent's own aggregation path (ParallelFanOut.Run -> JUnitCounts.Read) now sees
        // the whole worker, not its last slice.
        var totals = JUnitCounts.Read(junit);
        Assert.Equal(4, totals.Tests);
        Assert.Equal(1, totals.Errors);
        Assert.Equal(0, totals.Failures);
        Assert.Equal(0, totals.Skipped);
    }

    /// <summary>
    /// #2716 negative: with resume disabled the same fixture writes exactly attempt 1's two
    /// cases — nothing is carried in from nowhere, and the count is not inflated.
    /// </summary>
    [SkippableFact]
    public void NonResumedRun_JUnitHoldsOnlyWhatThisProcessRan()
    {
        TestArtifacts.SkipIfMissing();
        var junit = Path.Combine(_resumeRoot, "out", "single.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(junit)!);

        var (output, exit) = RunRunner(_resumeRoot, 180_000,
            "--test-timeout 2", "--resume-aborts 0", $"--output-junit \"{junit}\"");

        Assert.NotEqual(0, exit);
        Assert.DoesNotContain("resume: a watchdog abort ended this attempt early", output);
        Assert.DoesNotContain("carried from earlier attempt(s)", output);

        var names = TestCases(junit).Select(c => c.Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "Hangs", "RanBeforeHang" }, names);
        var totals = JUnitCounts.Read(junit);
        Assert.Equal(2, totals.Tests);
        Assert.Equal(1, totals.Errors);
    }
}
