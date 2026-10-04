// JobsReportsEndToEndTests — a `--jobs` run writes the report files the caller named, and they hold what the
// same layout in ONE process holds (#5129): `--out` (every worker used to write the one path and the last to
// finish won), `--output-json` (stdout carried no document at all), `--output-junit`, and the `exitCode` field /
// `Result:` line under `--no-strict-exit`.
//
// Fixtures/JobsOutputs: `a` and `b` (one passing and one failing test each, one worker apiece), `shared` (four
// codeunits that sleep, two failing, so two workers share the bundle). The oracle in every test is the plain
// run of the same folders, parsed as JSON / XML, never searched as text. One cache root serves every run.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace AlRunner.Tests;

public sealed class JobsReportsEndToEndTests
{
    private sealed record Run(int Exit, string Stdout, string Stderr, string Out, string Junit);

    private static readonly string Scratch = TestScratch.Dir("al-runner-jobs-reports");
    private static readonly string Cache = Path.Combine(Scratch, "cache");

    private static string A => ResumeRun.Fixture("JobsOutputs/a");
    private static string B => ResumeRun.Fixture("JobsOutputs/b");
    private static string Shared => ResumeRun.Fixture("JobsOutputs/shared");

    /// <summary>One runner process with the three report flags (each only when `reports` is set), its stdout kept
    /// apart from its stderr: --output-json is the whole of stdout. The environment goes through the child's own
    /// start info, with a free-memory reading and a split floor that let the small `shared` fixture be shared.</summary>
    private static Run Go(string name, string flags, bool reports, params string[] folders)
    {
        var dir = Path.Combine(Scratch, name);
        Directory.CreateDirectory(dir);
        var outPath = Path.Combine(dir, "out", "classification.json");   // the directory does not exist yet
        var junit = Path.Combine(dir, "junit.xml");
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var cmd = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(root, "AlRunner")));
        cmd.Append(TestBuildConfig.BcVersionArg).Append($" --cache \"{Cache}\" {flags}");
        if (reports) cmd.Append($" --output-json --out \"{outPath}\" --output-junit \"{junit}\"");
        foreach (var f in folders) cmd.Append($" \"{f}\"");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = cmd.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
        };
        psi.Environment["AL_RUNNER_JOBS_SPLIT_MIN_FILES"] = "1";
        psi.Environment[AlRunner.Infrastructure.JobsMemory.FreeMemoryEnvVar] = "1000000";
        var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(600_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        return new Run(p.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult(),
            File.Exists(outPath) ? File.ReadAllText(outPath) : "", File.Exists(junit) ? File.ReadAllText(junit) : "");
    }

    private static readonly Lazy<Run> Plain = new(() => Go("plain", "", reports: true, A, B));
    private static readonly Lazy<Run> Jobs = new(() => Go("jobs", "--jobs 2", reports: true, A, B));
    private static readonly Lazy<Run> JobsNoStrict = new(() => Go("jobs-no-strict", "--jobs 2 --no-strict-exit --output-json", reports: false, A, B));
    private static readonly Lazy<Run> PlainShared = new(() => Go("plain-shared", "", reports: true, Shared));
    private static readonly Lazy<Run> JobsShared = new(() => Go("jobs-shared", "--jobs 2", reports: true, Shared));

    // ── the report files, parsed ────────────────────────────────────────────────────────────

    private static JsonDocument Doc(string text)
    {
        Assert.False(string.IsNullOrWhiteSpace(text), "the report was empty: it is missing, or it went somewhere else");
        return JsonDocument.Parse(text);
    }

    private static List<string> TestNames(Run r)
    {
        using var d = Doc(r.Stdout);
        return d.RootElement.GetProperty("tests").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToList();
    }

    private static List<(string Name, string Status)> Statuses(Run r)
    {
        using var d = Doc(r.Stdout);
        return d.RootElement.GetProperty("tests").EnumerateArray()
            .Select(t => (t.GetProperty("name").GetString()!, t.GetProperty("status").GetString()!)).ToList();
    }

    private static List<string> Failures(Run r)
    {
        using var d = Doc(r.Out);
        return d.RootElement.GetProperty("all_failures").EnumerateArray()
            .Select(f => string.Join("|", f.GetProperty("kind").GetString(), Path.GetFileName(f.GetProperty("bucket").GetString()),
                f.GetProperty("codeunit").GetString(), f.GetProperty("method").GetString(), f.GetProperty("message").GetString(),
                f.GetProperty("classification").GetString()))
            .OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    private static List<string> JUnitCases(Run r)
        => XDocument.Parse(r.Junit).Descendants("testcase")
            .Select(c => $"{c.Attribute("classname")!.Value}.{c.Attribute("name")!.Value}:{(c.Elements("failure").Any() ? "fail" : "pass")}")
            .OrderBy(s => s, StringComparer.Ordinal).ToList();

    private static int Int(JsonDocument d, string property) => d.RootElement.GetProperty(property).GetInt32();

    // ── plain run: the oracle ───────────────────────────────────────────────────────────────

    [SkippableFact]
    public void Oracle_APlainRunWritesAllThreeReports_AndTheFailuresAreTheTwoDeliberateOnes()
    {
        TestArtifacts.SkipIfMissing();
        var plain = Plain.Value;

        Assert.Equal(1, plain.Exit);
        Assert.Equal(new[] { "Codeunit65760.A_Passes", "Codeunit65760.A_FailsOnPurpose", "Codeunit65770.B_Passes", "Codeunit65770.B_FailsOnPurpose" },
            TestNames(plain));
        Assert.Equal(2, Failures(plain).Count);
        Assert.Equal(4, JUnitCases(plain).Count);
    }

    // ── --out ───────────────────────────────────────────────────────────────────────────────

    /// <summary>`--out` held only the last worker to finish: one failure of two (`total_failures: 1`, bundle b
    /// only). It holds both workers' failures, each once, as the plain run lists them; the directory of the path
    /// did not exist, and the parent made it.</summary>
    [SkippableFact]
    public void Out_HoldsEveryWorkersFailures_EachOnce_AsThePlainRunDoes()
    {
        TestArtifacts.SkipIfMissing();
        var jobs = Jobs.Value;

        Assert.False(string.IsNullOrEmpty(jobs.Out), $"--out was not written.\n{jobs.Stderr}");
        using var doc = Doc(jobs.Out);
        Assert.Equal(2, Int(doc, "total_failures"));
        Assert.Equal(Failures(Plain.Value), Failures(jobs));
        Assert.Contains("Classification -> ", jobs.Stderr);
        Assert.Contains("(merged from 2 worker(s))", jobs.Stderr);
        // no worker wrote the path itself (its line is "Classification → <path>"): the parent's write comes last
        // and would hide it, so the file alone cannot show a worker that still did
        Assert.DoesNotContain("Classification → ", jobs.Stderr);
    }

    // ── --output-json ───────────────────────────────────────────────────────────────────────

    /// <summary>stdout was EMPTY under `--jobs` (the whole run went to stderr). It is one JSON document, parsed
    /// whole, with the plain run's tests in the plain run's order (the caller's bundle order, not shard order),
    /// its counters, and `exitCode` equal to the process's.</summary>
    [SkippableFact]
    public void OutputJson_IsOneDocumentOnStdout_WithThePlainRunsTestsCountersAndExitCode()
    {
        TestArtifacts.SkipIfMissing();
        var jobs = Jobs.Value;
        var plain = Plain.Value;

        Assert.False(string.IsNullOrWhiteSpace(jobs.Stdout), $"stdout carried no --output-json document.\n{jobs.Stderr}");
        using var j = Doc(jobs.Stdout);
        using var p = Doc(plain.Stdout);
        Assert.Equal(Statuses(plain), Statuses(jobs));
        foreach (var counter in new[] { "passed", "failed", "errors", "skipped", "total" })
            Assert.Equal(Int(p, counter), Int(j, counter));
        Assert.Equal(1, Int(j, "exitCode"));
        Assert.Equal(jobs.Exit, Int(j, "exitCode"));
        Assert.Equal(Int(p, "exitCode"), Int(j, "exitCode"));
    }

    /// <summary>The worker's own output is text on stderr now, not a second and third document: the parent's
    /// aggregate line is still there, and the JSON is the only thing on stdout.</summary>
    [SkippableFact]
    public void OutputJson_StdoutHoldsNothingBesidesTheDocument_AndTheAggregateStaysOnStderr()
    {
        TestArtifacts.SkipIfMissing();
        var jobs = Jobs.Value;

        Assert.StartsWith("{", jobs.Stdout.TrimStart());
        Assert.EndsWith("}", jobs.Stdout.TrimEnd());
        Assert.Contains("aggregate across 2 worker process(es)", jobs.Stderr);
        Assert.Contains("Tests: 4   passed 2   failed 2   errors 0   skipped 0", jobs.Stderr);
    }

    // ── --output-junit, the one that already worked ─────────────────────────────────────────

    [SkippableFact]
    public void OutputJunit_StillHoldsEveryCase_AlongsideTheOtherTwoReports()
    {
        TestArtifacts.SkipIfMissing();
        var jobs = Jobs.Value;

        Assert.Equal(JUnitCases(Plain.Value), JUnitCases(jobs));
        Assert.Equal("4", XDocument.Parse(jobs.Junit).Root!.Attribute("tests")!.Value);
    }

    // ── --no-strict-exit ────────────────────────────────────────────────────────────────────

    /// <summary>A worker forced to exit 0 hid its verdict: the aggregate printed `Result: PASSED` over two failed
    /// tests and the document's `exitCode` read 0. The run's own code is 1 (the plain document says so), the
    /// process exits 0 as asked, and the Result line says both.</summary>
    [SkippableFact]
    public void NoStrictExit_TheProcessExitsZero_ButTheDocumentAndTheResultLineKeepTheRunsOwnCode()
    {
        TestArtifacts.SkipIfMissing();
        var run = JobsNoStrict.Value;

        Assert.Equal(0, run.Exit);
        using var doc = Doc(run.Stdout);
        using var plain = Doc(Plain.Value.Stdout);
        Assert.Equal(2, Int(doc, "failed"));
        Assert.Equal(Int(plain, "exitCode"), Int(doc, "exitCode"));
        Assert.Contains("Result: FAILED, exit code 0 (--no-strict-exit; the run's own code is 1", run.Stderr);
        Assert.DoesNotContain("Result: PASSED", run.Stderr);
    }

    // ── controls: nothing to fan out, nothing to write ──────────────────────────────────────

    /// <summary>One light bundle is not fanned out, so `--jobs 2` leaves the run in this process exactly as before:
    /// the reports are the single-process writers', each with the bundle's own two tests and one failure.</summary>
    [SkippableFact]
    public void OneLightBundleUnderJobs_IsNotFannedOut_AndItsReportsAreTheSingleProcessOnes()
    {
        TestArtifacts.SkipIfMissing();
        var run = Go("jobs-one-bundle", "--jobs 2", reports: true, A);

        Assert.Equal(1, run.Exit);
        Assert.DoesNotContain("worker process(es)", run.Stdout + run.Stderr);
        Assert.Equal(new[] { "Codeunit65760.A_Passes", "Codeunit65760.A_FailsOnPurpose" }, TestNames(run));
        Assert.Single(Failures(run));
        Assert.Equal(2, JUnitCases(run).Count);
        Assert.DoesNotContain("merged from", run.Stdout + run.Stderr);
    }

    /// <summary>A path the run cannot write is refused before any worker starts, as for a single process (#2403): exit 2,
    /// the flag named, no plan line. The parent's own late write cannot be the first to find out.</summary>
    [SkippableFact]
    public void AnUnusableOutPath_IsRefusedBeforeTheFanOut()
    {
        TestArtifacts.SkipIfMissing();
        var blocker = Path.Combine(Scratch, "blocker");
        File.WriteAllText(blocker, "a file where --out needs a directory");
        var run = Go("jobs-unusable-out", $"--jobs 2 --out \"{Path.Combine(blocker, "results.json")}\"", reports: false, A, B);

        Assert.Equal(2, run.Exit);
        Assert.Contains("--out '", run.Stdout + run.Stderr);
        Assert.Contains("is not a usable output path", run.Stdout + run.Stderr);
        Assert.DoesNotContain("worker process(es)", run.Stdout + run.Stderr);
    }

    // ── a bundle several workers share ──────────────────────────────────────────────────────

    /// <summary>One bundle used to stay in one process whenever it passed --out or --output-json, because each worker
    /// would have written its own. Now the bundle is shared, every test runs once, and the reports hold what the
    /// plain run's do. Both workers must have run something, or the merge was never exercised.</summary>
    [SkippableFact]
    public void ASharedBundle_IsReportedOnce_WithEveryWorkersTestsAndFailures()
    {
        TestArtifacts.SkipIfMissing();
        var jobs = JobsShared.Value;
        var plain = PlainShared.Value;

        Assert.Contains("is shared by 2 worker(s)", jobs.Stdout + jobs.Stderr);
        var shardTests = System.Text.RegularExpressions.Regex.Matches(jobs.Stderr, @"^Tests: (\d+)   passed", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.Equal(new[] { 4 }, shardTests.Skip(2).Take(1));   // the aggregate line follows the two shard lines
        Assert.True(shardTests.Take(2).All(c => c > 0), $"a worker ran nothing: {string.Join("/", shardTests)}");

        var names = TestNames(jobs);
        Assert.Equal(4, names.Count);
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Equal(TestNames(plain).OrderBy(n => n, StringComparer.Ordinal), names.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(Statuses(plain).OrderBy(s => s.Name, StringComparer.Ordinal), Statuses(jobs).OrderBy(s => s.Name, StringComparer.Ordinal));
        Assert.Equal(Failures(plain), Failures(jobs));
        Assert.Equal(JUnitCases(plain), JUnitCases(jobs));
        Assert.Equal(1, jobs.Exit);
    }

    // ── a worker that resumes after a watchdog abort ────────────────────────────────────────

    /// <summary>The worker that hangs resumes in a fresh process and re-runs what its first attempt did not
    /// reach; the document and `--out` name each bundle once, as the single-process resume does
    /// (ResumeStructuredOutputsTests, the same three fixtures: four records, one suite error per bundle that
    /// lost a suite, the abort kept beside the drop).</summary>
    [SkippableFact]
    public void AWorkerThatResumes_ContributesItsBundlesOnce_ToOutAndOutputJson()
    {
        TestArtifacts.SkipIfMissing();
        var run = Go("jobs-resume", "--jobs 2 --test-timeout 3", reports: true,
            ResumeRun.Fixture("ResumeEmitExcluded"), ResumeRun.Fixture("ResumeEmitExcludedPartner"), ResumeRun.Fixture("ResumeEmitExcludedNoTests"));

        Assert.Contains("resume: a watchdog abort ended this attempt early", run.Stderr);
        Assert.Equal(3, run.Exit);
        using var json = Doc(run.Stdout);
        var suite = ResumeRun.BundleErrors(json, "suiteErrors");
        Assert.Equal(new[] { "ResumeEmitExcluded", "ResumeEmitExcludedPartner" }, suite.Select(s => s.Bundle).OrderBy(b => b, StringComparer.Ordinal));
        var hanging = suite.Single(s => s.Bundle == "ResumeEmitExcluded").Errors;
        Assert.Equal(2, hanging.Count);
        Assert.Single(hanging, e => e.Contains("TEST-TIMEOUT-ABORT"));
        Assert.Single(suite.Single(s => s.Bundle == "ResumeEmitExcludedPartner").Errors);
        Assert.Equal(new[] { "ResumeEmitExcludedNoTests" }, ResumeRun.BundleErrors(json, "compilationErrors").Select(c => c.Bundle));
        Assert.Equal(3, Int(json, "exitCode"));

        using var outDoc = Doc(run.Out);
        Assert.Equal(4, Int(outDoc, "total_failures"));
    }

    // ── --tdd ───────────────────────────────────────────────────────────────────────────────

    /// <summary>`--tdd` under `--jobs` over a shared bundle with a dependency-only folder (#5262, #5318): the document
    /// holds the plain `--tdd` run's tests, and the member `--tdd` generated for a test still shows on it
    /// (`generatedStubs` crosses from the worker with the rest of its result).</summary>
    [SkippableFact]
    public void TddUnderJobs_OutputJson_HoldsThePlainTddRunsTests_WithTheirGeneratedStubs()
    {
        TestArtifacts.SkipIfMissing();
        var app = ResumeRun.Fixture("JobsTddRerun/app");
        var test = ResumeRun.Fixture("JobsTddRerun/test");
        var plain = Go("tdd-plain", "--tdd --output-json", reports: false, app, test);
        var jobs = Go("tdd-jobs", "--jobs 2 --tdd --output-json", reports: false, app, test);

        Assert.Equal(1, plain.Exit);
        Assert.Equal(1, jobs.Exit);
        Assert.Contains("is shared by 2 worker(s)", jobs.Stdout + jobs.Stderr);
        List<(string Name, string Status, string Stubs)> Rows(Run run)
        {
            using var d = Doc(run.Stdout);
            return d.RootElement.GetProperty("tests").EnumerateArray()
                .Select(t => (t.GetProperty("name").GetString()!, t.GetProperty("status").GetString()!,
                    t.TryGetProperty("generatedStubs", out var s) ? string.Join(";", s.EnumerateArray().Select(x => x.GetString())) : ""))
                .OrderBy(r => r.Item1, StringComparer.Ordinal).ToList();
        }
        var plainRows = Rows(plain);
        Assert.Equal(8, plainRows.Count);
        Assert.Contains(plainRows, r => r.Stubs.Contains("CalcLit"));   // the premise: the plain run reports a generated member
        Assert.Equal(plainRows, Rows(jobs));
    }
}
