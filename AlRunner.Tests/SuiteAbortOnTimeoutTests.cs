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
using System.Text.Json;
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
        _root = TestScratch.Dir("al-runner-suite-abort-on-timeout");
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
    /// Same spawn, but keeping stdout and stderr APART. Every other test here reads them merged,
    /// which is fine when it is looking for a message — but --output-json's whole contract is
    /// about what lands on stdout ALONE, and a merged capture cannot tell a second JSON document
    /// from an ordinary stderr line following the first (#2719).
    /// </summary>
    private (string stdout, string stderr, int exit) RunRunnerSplit(
        string bundle, int waitMs, params string[] extraArgs)
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
        var so = new StringBuilder();
        var se = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (so) so.AppendLine(e.Data); };
        p.ErrorDataReceived  += (_, e) => { if (e.Data != null) lock (se) se.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(waitMs)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (so) lock (se) return (so.ToString(), se.ToString(), p.ExitCode);
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
    /// The precondition every assertion in the two resume tests rests on: the fixture's
    /// <c>Hangs</c> test really did hang, the per-test watchdog really did fire, and that really
    /// did escalate to a suite abort.
    ///
    /// It is asserted separately, and first, because when it does NOT hold the consequences fail
    /// in a shape that describes the wrong problem. On CI (issue #2801, seen on BC 28.4 across
    /// two unrelated PRs) these tests failed as a bare collection diff —
    /// <c>Expected ["Hangs","RanBeforeHang"]</c> vs <c>Actual [... ,"SecondA","SecondB"]</c> —
    /// and as a missing <c>resume:</c> line. Both reduce to one fact: <c>Hangs</c> did not report
    /// <c>TimedOut</c>, so <c>TestExecutor</c> never took its abort path
    /// (<c>if (IsTimeout(raw)) { RecordAbortedSuite(...); return results; }</c>), so the run
    /// carried on into the second codeunit AND there was nothing for a resume to resume.
    ///
    /// What made <c>Hangs</c> fail to time out on those legs is NOT established. Anything that
    /// makes it fail FAST instead of hanging produces exactly this shape, and #2801 stays open on
    /// that question — this method does not fix the flake, it makes the next occurrence say what
    /// happened instead of nothing.
    ///
    /// The whole runner output goes into the failure message on purpose: the assertions below
    /// compare name lists and discard <c>output</c>, which is why the CI failures carried no
    /// evidence at all. xUnit truncates its own previews, so <c>Assert.Contains</c> is not enough
    /// here.
    /// </summary>
    private static void AssertHangEscalatedToAbort(string output, int timeoutSeconds)
    {
        var timedOut = $"Test exceeded {timeoutSeconds}s timeout.";
        Assert.True(output.Contains(timedOut, StringComparison.Ordinal),
            $"PRECONDITION FAILED: the fixture's Hangs test never hit the {timeoutSeconds}s per-test "
            + $"watchdog, so \"{timedOut}\" is absent. Nothing asserted after this point is "
            + "interpretable: with no timeout there is no suite abort, so the run continues into "
            + "the next codeunit and no resume is triggered. This is issue #2801, and the cause of "
            + "the missing timeout is still unknown — see it before reading the run below as a "
            + "resume/JUnit defect."
            + "\n--- runner output ---\n" + output);

        Assert.True(output.Contains("SUITE ABORTED", StringComparison.Ordinal),
            "PRECONDITION FAILED: Hangs timed out but TestExecutor did not escalate it to a suite "
            + "abort, so the rest of the run was never abandoned. That is a different defect from "
            + "the timeout not firing at all, and it is the half #2801 has never observed."
            + "\n--- runner output ---\n" + output);
    }

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
        // Before anything resume-specific: did the hang hang, and did that abort the suite?
        // Without this the failure below reads as a resume defect when it is not one (#2801).
        AssertHangEscalatedToAbort(output, timeoutSeconds: 2);
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
    /// #2719: --output-json, --out and --count-baseline must describe the RUN after a resume,
    /// not the final attempt's slice. Before this the same command produced, all at once:
    /// TWO JSON documents concatenated on stdout (so json.loads failed outright); a final
    /// document reading total=2 passed=2 errors=0 exitCode=0 — a completely clean run — while
    /// the process exited non-zero; a classification file claiming total_failures=0; and a
    /// count-baseline DROP reported in the same log as "4 total".
    ///
    /// One spawn, all four outputs, because they have to AGREE — that is the actual claim.
    /// </summary>
    [SkippableFact]
    public void ResumedRun_JsonClassificationAndBaseline_DescribeTheWholeRun()
    {
        TestArtifacts.SkipIfMissing();
        var dir = Path.Combine(_resumeRoot, "out2");
        Directory.CreateDirectory(dir);
        var jsonPath = Path.Combine(dir, "stdout.json");
        var clsPath = Path.Combine(dir, "cls.json");
        var junitPath = Path.Combine(dir, "r.xml");
        var baselinePath = Path.Combine(dir, "baseline.json");
        // The whole run is 4 tests; a baseline of 4 must be MET, not reported as a drop.
        File.WriteAllText(baselinePath,
            $$"""{ "suites": { "{{Path.GetFileName(_resumeRoot)}}": { "tests": { "default": 4 } } } }""");

        var (stdout, stderr, exit) = RunRunnerSplit(_resumeRoot, 300_000,
            "--test-timeout 2", "--resume-aborts 1", "--output-json",
            $"--out \"{clsPath}\"", $"--output-junit \"{junitPath}\"",
            $"--count-baseline \"{baselinePath}\"");

        Assert.NotEqual(0, exit);
        Assert.Contains("resume: a watchdog abort ended this attempt early", stderr);

        // stdout is ONE json document. A second one is not "the wrong half" — it is unparseable.
        var stdoutJson = StdoutJson(stdout);
        using var doc = JsonDocument.Parse(stdoutJson);
        var root = doc.RootElement;

        Assert.Equal(4, root.GetProperty("total").GetInt32());
        Assert.Equal(3, root.GetProperty("passed").GetInt32());
        Assert.Equal(1, root.GetProperty("errors").GetInt32());
        // The field a consumer trusts INSTEAD of the exit code. It said 0 for a failed run.
        Assert.Equal(exit, root.GetProperty("exitCode").GetInt32());

        var names = root.GetProperty("tests").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!.Split('.').Last())
            .OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "Hangs", "RanBeforeHang", "SecondA", "SecondB" }, names);

        // The carried error reaches the classification file, which reported zero failures.
        var cls = JsonDocument.Parse(File.ReadAllText(clsPath)).RootElement;
        Assert.True(cls.GetProperty("total_failures").GetInt32() > 0,
            "the classification file must not report a resumed run with an error as having no failures");

        // The baseline the whole run meets is met, and the phantom DROP is gone.
        Assert.DoesNotContain("[count-baseline] DROP", stderr);
        Assert.Contains("[count-baseline] skipped: this attempt is resuming", stderr);

        // And --output-junit is untouched by all of this: still 4 cases, not 8. The carried
        // attempt must not be counted once per output shape.
        Assert.Equal(4, TestCases(junitPath).Count);
    }

    /// <summary>The single JSON document the runner prints on stdout. Fails loudly, naming what
    /// it saw, rather than letting a test read the first of two concatenated documents.</summary>
    private static string StdoutJson(string stdout)
    {
        var start = stdout.IndexOf('{');
        Assert.True(start >= 0, "no JSON found on stdout:\n" + stdout);
        var text = stdout.Substring(start).Trim();
        var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(text), isFinalBlock: true,
            state: default);
        Assert.True(JsonDocument.TryParseValue(ref reader, out _),
            "stdout did not hold ONE parseable JSON document:\n" + text);
        var consumed = (int)reader.BytesConsumed;
        var trailing = System.Text.Encoding.UTF8.GetString(
            System.Text.Encoding.UTF8.GetBytes(text), consumed,
            System.Text.Encoding.UTF8.GetByteCount(text) - consumed).Trim();
        Assert.True(trailing.Length == 0,
            "stdout held MORE than one JSON document — a consumer's json.loads fails outright. "
            + "Trailing:\n" + trailing);
        return text;
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
        AssertHangEscalatedToAbort(output, timeoutSeconds: 2);
        Assert.DoesNotContain("resume: a watchdog abort ended this attempt early", output);
        Assert.DoesNotContain("carried from earlier attempt(s)", output);

        var names = TestCases(junit).Select(c => c.Name).OrderBy(n => n).ToList();
        Assert.True(names.SequenceEqual(new[] { "Hangs", "RanBeforeHang" }),
            "JUnit must hold exactly attempt 1's two cases. Got: ["
            + string.Join(", ", names) + "]. With --resume-aborts 0 the abort ends the run, so "
            + "SecondA/SecondB appearing here means the run continued past the hang."
            + "\n--- runner output ---\n" + output);
        var totals = JUnitCounts.Read(junit);
        Assert.Equal(2, totals.Tests);
        Assert.Equal(1, totals.Errors);
    }
}
