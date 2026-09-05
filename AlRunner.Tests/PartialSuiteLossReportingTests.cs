// PartialSuiteLossReportingTests — issue #2762.
//
// A bundle whose AL fails to compile sheds the objects that did not compile and carries on.
// Program.cs already routes that into `bundleErrors` and into the exit code: computedExitCode
// counts `b.CompileErrors.Count > 0` on a bucket whose Stage is still `Ran` (the fix
// BundleSuiteErrorLoudnessTests pins). So the VERDICT is correct — exit 3.
//
// What no surface reported is the REASON. Every reporting path branches on `Stage` and reads
// `CompileErrors` only for a CompileFailed / ExecuteFailed bucket, and a bucket whose sibling
// suite still produced one passing test has Stage == Ran. Measured on the real runner before
// this change, against Fixtures/EmitExcludedPartialBundle (1 object EMIT-EXCLUDED, 2 [Test]
// procedures dropped with its module, 1 healthy sibling suite):
//
//     Buckets:       1 total
//       ran:         1
//       compile-fail:0
//       exec-fail:   0
//     Tests:         1 total
//       pass:        1
//       fail:        0
//       error:       0
//
// — byte-identical to a clean one-test run. The only disagreement was the exit code, and
// `--watch` has no exit code at all, so there the loss was completely silent.
//
// This file is entirely about the RUNNER's own reporting. There is no claim about Business
// Central anywhere in it — "what does al-runner's summary block print when it cannot emit an
// object" is not a question a service tier can adjudicate — so nothing here belongs in the
// al-language corpus (.claude/rules/bc-behavior-tests-go-upstream.md).
//
// The negatives are what make the positives mean something: a section printed unconditionally,
// or a count that folded lost suites into the test totals, would satisfy "the loss is named"
// and still be wrong.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public sealed class PartialSuiteLossReportingTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string PartialBundle = Path.Combine(
        RepoRoot, "AlRunner.Tests", "Fixtures", "EmitExcludedPartialBundle");

    private const string SuiteError =
        "<bundled>: EMIT-EXCLUDED for Contoso Widgets: 23 object(s) dropped from the module "
        + "— tests they declare are missing: [Codeunit \"Widget Tests\"].";

    private static TestResult Pass(string codeunit, string method) =>
        new(codeunit, method, TestOutcome.Pass, null, null, TimeSpan.FromMilliseconds(3));

    /// <summary>A bucket that RAN — one surviving pass — but lost a suite to a compile error.</summary>
    private static BucketResult PartialLossBucket(string path = "/bundle-a") =>
        new(path, BucketStage.Ran,
            new[] { SuiteError }, null, new[] { Pass("Codeunit61360", "PartialBundleHealthy_StillRuns") },
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1, null);

    /// <summary>The same bucket with nothing lost — the control for every negative below.</summary>
    private static BucketResult CleanBucket(string path = "/bundle-a") =>
        new(path, BucketStage.Ran,
            Array.Empty<string>(), null, new[] { Pass("Codeunit61360", "PartialBundleHealthy_StillRuns") },
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1, null);

    private static string Summarize(params BucketResult[] buckets)
    {
        var w = new StringWriter();
        Reporter.PrintSummary(buckets, w);
        return w.ToString();
    }

    private static string PerTest(bool showPass, params BucketResult[] buckets)
    {
        var w = new StringWriter();
        Reporter.PrintPerTest(buckets, w, showPass);
        return w.ToString();
    }

    // ── Summary ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The summary is where a human scrolling to the bottom and a scripted caller both look.
    /// It must name the lost suite VERBATIM — the message already names the API (EMIT-EXCLUDED),
    /// the module and the dropped objects, and paraphrasing it sends the reader back up the log
    /// (.claude/rules/loud-failures.md).
    /// </summary>
    [Fact]
    public void Summary_RanBucketThatLostASuite_NamesItAndCountsIt()
    {
        var summary = Summarize(PartialLossBucket());

        Assert.Contains("Suite errors: 1", summary, StringComparison.Ordinal);
        Assert.Contains(SuiteError, summary, StringComparison.Ordinal);
        // The bucket it happened in, so a multi-bundle run is actionable.
        Assert.Contains("/bundle-a", summary, StringComparison.Ordinal);
        // And the consequence stated in the summary itself, not inferred from the exit code.
        Assert.Contains("MISSING", summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// "ran: 1 / compile-fail: 0" on its own is what made the loss invisible. The bucket really
    /// did run, so it is not a compile-fail — it gets its own line instead.
    /// </summary>
    [Fact]
    public void Summary_RanBucketThatLostASuite_IsCountedAsPartialNotAsCompileFail()
    {
        var summary = Summarize(PartialLossBucket());

        Assert.Contains("partial:     1", summary, StringComparison.Ordinal);
        Assert.Contains("compile-fail:0", summary, StringComparison.Ordinal);
        Assert.Contains("ran:         1", summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// No inflation. The test counts must keep meaning "tests that actually ran" — a fix that
    /// synthesised the missing tests into the totals would make every count in the summary a
    /// number nobody measured.
    /// </summary>
    [Fact]
    public void Summary_RanBucketThatLostASuite_DoesNotInflateTheTestCounts()
    {
        var summary = Summarize(PartialLossBucket());

        Assert.Contains("Tests:         1 total", summary, StringComparison.Ordinal);
        Assert.Contains("pass:        1", summary, StringComparison.Ordinal);
        Assert.Contains("fail:        0", summary, StringComparison.Ordinal);
        Assert.Contains("error:       0", summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The negative that keeps a clean run's output unchanged. Printed unconditionally, this
    /// section would be noise on every run, and every integration test asserting on the
    /// summary's existing markers would be asserting past it.
    /// </summary>
    [Fact]
    public void Summary_CleanRun_HasNoSuiteErrorSectionAndNoPartialLine()
    {
        var summary = Summarize(CleanBucket());

        Assert.DoesNotContain("Suite errors", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("partial:", summary, StringComparison.Ordinal);
        Assert.Contains("Tests:         1 total", summary, StringComparison.Ordinal);
        Assert.Contains("pass:        1", summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// A CompileFailed bucket already had its errors printed by PrintPerTest's COMPILE FAIL
    /// block. Counting it as "partial" too would double-report it and misstate what happened:
    /// nothing in that bucket ran.
    /// </summary>
    [Fact]
    public void Summary_FullyCompileFailedBucket_IsNotCountedAsPartial()
    {
        var bucket = new BucketResult("/bundle-b", BucketStage.CompileFailed,
            new[] { SuiteError }, null, Array.Empty<TestResult>(),
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0, null);

        var summary = Summarize(bucket);

        Assert.Contains("compile-fail:1", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("partial:", summary, StringComparison.Ordinal);
    }

    // ── Per-test report ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The per-bucket report showed the surviving PASS lines and nothing else. The suite that
    /// never ran belongs next to them, or the reader concludes the bucket is complete.
    /// </summary>
    [Fact]
    public void PerTest_RanBucketThatLostASuite_PrintsTheErrorVerbatim()
    {
        var report = PerTest(showPass: true, PartialLossBucket());

        Assert.Contains("SUITE ERRORS (1)", report, StringComparison.Ordinal);
        Assert.Contains(SuiteError, report, StringComparison.Ordinal);
        // The survivors are still reported — a fix that replaced the bucket's output with the
        // error would trade one silent inaccuracy for another.
        Assert.Contains("PartialBundleHealthy_StillRuns", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sibling gap, and the reason the default output was completely empty: PrintPerTest
    /// skips a bucket with no VISIBLE tests, and at default verbosity a bucket whose survivors
    /// all passed has none. Without this the section exists but never prints on the runs that
    /// matter most — every all-green run that quietly lost a suite.
    /// </summary>
    [Fact]
    public void PerTest_AllSurvivorsPassedAtDefaultVerbosity_StillPrintsTheSuiteError()
    {
        var report = PerTest(showPass: false, PartialLossBucket());

        Assert.Contains("SUITE ERRORS (1)", report, StringComparison.Ordinal);
        Assert.Contains(SuiteError, report, StringComparison.Ordinal);
    }

    /// <summary>Negative: a clean bucket with only passes still prints nothing at all.</summary>
    [Fact]
    public void PerTest_CleanBucketAtDefaultVerbosity_PrintsNothing()
    {
        var report = PerTest(showPass: false, CleanBucket());

        Assert.DoesNotContain("SUITE ERRORS", report, StringComparison.Ordinal);
        Assert.Equal("", report);
    }

    // ── --output-json ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Same defect one layer down, and the same fix #2779 applied to ExecuteFailed buckets: a
    /// consumer reading --output-json saw `compilationErrors: null` and a fully passing test
    /// list. The exitCode field said 3 and the document explained nothing.
    /// </summary>
    [Fact]
    public void Json_RanBucketThatLostASuite_CarriesItAsSuiteErrors()
    {
        var json = Reporter.SerializeJsonOutput(new[] { PartialLossBucket() }, exitCode: 3);
        using var doc = JsonDocument.Parse(json);

        var suiteErrors = doc.RootElement.GetProperty("suiteErrors");
        Assert.Equal(1, suiteErrors.GetArrayLength());
        var entry = suiteErrors[0];
        Assert.Equal("/bundle-a", entry.GetProperty("file").GetString());
        Assert.Equal(SuiteError, entry.GetProperty("errors")[0].GetString());

        // The surviving test is still in `tests`, and the totals still count only what ran.
        Assert.Equal(1, doc.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("passed").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("exitCode").GetInt32());
    }

    /// <summary>
    /// Negative: null-omitted, so a run that lost nothing serialises exactly as before and no
    /// existing consumer sees a new empty array.
    /// </summary>
    [Fact]
    public void Json_CleanRun_OmitsSuiteErrorsEntirely()
    {
        var json = Reporter.SerializeJsonOutput(new[] { CleanBucket() }, exitCode: 0);
        using var doc = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.TryGetProperty("suiteErrors", out _));
    }

    // ── --out classification file ────────────────────────────────────────────────────

    /// <summary>
    /// WriteClassification's `else` branch (the Ran bucket) walked only failing TESTS, so a
    /// bucket that lost a whole suite contributed zero failure records — the file a triage
    /// pass reads said the run had nothing wrong with it.
    /// </summary>
    [Fact]
    public void Classification_RanBucketThatLostASuite_RecordsIt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"al-runner-cls-{Guid.NewGuid():N}.json");
        try
        {
            Reporter.WriteClassification(new[] { PartialLossBucket() }, path);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));

            Assert.Equal(1, doc.RootElement.GetProperty("total_failures").GetInt32());
            var failure = doc.RootElement.GetProperty("all_failures")[0];
            Assert.Equal("suite", failure.GetProperty("kind").GetString());
            Assert.Equal("/bundle-a", failure.GetProperty("bucket").GetString());
            Assert.Equal(SuiteError, failure.GetProperty("errors")[0].GetString());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>Negative: a clean run still writes an empty failure list.</summary>
    [Fact]
    public void Classification_CleanRun_RecordsNoFailures()
    {
        var path = Path.Combine(Path.GetTempPath(), $"al-runner-cls-{Guid.NewGuid():N}.json");
        try
        {
            Reporter.WriteClassification(new[] { CleanBucket() }, path);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(0, doc.RootElement.GetProperty("total_failures").GetInt32());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── --watch dashboard ────────────────────────────────────────────────────────────

    /// <summary>
    /// `--watch` has no exit code, so the dashboard IS the verdict. Its roll-up already counts
    /// a whole compile-failed bucket as one error; a suite lost from a bucket that otherwise
    /// ran got no such treatment and the footer read a clean 1P/0F/0E.
    /// </summary>
    [Fact]
    public void WatchTally_RanBucketThatLostASuite_CountsAsAnError()
    {
        var (pass, fail, err, total) = WatchDashboard.Tally(new[] { PartialLossBucket() });

        Assert.Equal(1, pass);
        Assert.Equal(0, fail);
        Assert.Equal(1, err);
        Assert.Equal(2, total);
    }

    /// <summary>Negative: a clean bucket's roll-up is untouched.</summary>
    [Fact]
    public void WatchTally_CleanBucket_IsUnchanged()
    {
        var (pass, fail, err, total) = WatchDashboard.Tally(new[] { CleanBucket() });

        Assert.Equal(1, pass);
        Assert.Equal(0, fail);
        Assert.Equal(0, err);
        Assert.Equal(1, total);
    }

    // ── --watch dashboard TREE (rendered) ────────────────────────────────────────────

    /// <summary>
    /// Renders the real dashboard through Spectre's TestConsole, the harness
    /// WatchDashboardTests already uses. Wide profile so the node text under test is never
    /// wrapped or truncated away — an assertion that passes only because a line was cut is
    /// worse than no assertion.
    /// </summary>
    private static string RenderWatch(params BucketResult[] results)
    {
        var console = new Spectre.Console.Testing.TestConsole();
        console.Profile.Width = 200;
        console.Write(WatchDashboard.Build(results, "my-bundle", WatchStatus.Idle,
            new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Local), TimeSpan.FromSeconds(1)));
        return console.Output;
    }

    /// <summary>
    /// Tally counts a lost suite, but the count alone is a number with no name attached — under
    /// --watch the tree is where the developer finds out WHICH suite went missing and why, and
    /// there is no exit code to send them looking. Asserts the concrete node text, not merely
    /// that something rendered.
    /// </summary>
    [Fact]
    public void WatchTree_RanBucketThatLostASuite_RendersANamedSuiteErrorsNode()
    {
        var output = RenderWatch(PartialLossBucket("/tmp/my-bundle"));

        // The node header, carrying the bucket name and how many suites it lost.
        Assert.Contains("my-bundle", output, StringComparison.Ordinal);
        Assert.Contains("SUITE ERRORS (1)", output, StringComparison.Ordinal);

        // The message itself, so the reader learns the surface (EMIT-EXCLUDED) and the module
        // from the tree rather than from a stderr line the dashboard has already overpainted.
        Assert.Contains("EMIT-EXCLUDED for Contoso Widgets", output, StringComparison.Ordinal);
        Assert.Contains("23 object(s) dropped from the module", output, StringComparison.Ordinal);

        // And the consequence spelled out, because a cycle has no exit code to state it.
        Assert.Contains("MISSING from this cycle, not passing", output, StringComparison.Ordinal);

        // The surviving test is still in the tree — a node that replaced the bucket's results
        // would satisfy everything above and hide the run.
        Assert.Contains("PartialBundleHealthy_StillRuns", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The negative control. Without it every assertion above is satisfied by an implementation
    /// that emits the node unconditionally, which would put a red SUITE ERRORS banner on every
    /// clean --watch cycle.
    /// </summary>
    [Fact]
    public void WatchTree_CleanBucket_RendersNoSuiteErrorsNode()
    {
        var output = RenderWatch(CleanBucket("/tmp/my-bundle"));

        Assert.DoesNotContain("SUITE ERRORS", output, StringComparison.Ordinal);
        Assert.DoesNotContain("MISSING from this cycle", output, StringComparison.Ordinal);
        // …and the cycle's real results are untouched.
        Assert.Contains("PartialBundleHealthy_StillRuns", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A bucket that lost EVERYTHING already rendered as COMPILE FAILED. It must keep doing so
    /// rather than acquiring a second, contradictory node — the new branch is for buckets that
    /// ran, and Stage is the thing that separates them.
    /// </summary>
    [Fact]
    public void WatchTree_FullyCompileFailedBucket_StillRendersCompileFailedNotSuiteErrors()
    {
        var bucket = new BucketResult("/tmp/my-bundle", BucketStage.CompileFailed,
            new[] { SuiteError }, null, Array.Empty<TestResult>(),
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0, null);

        var output = RenderWatch(bucket);

        Assert.Contains("COMPILE FAILED", output, StringComparison.Ordinal);
        Assert.DoesNotContain("SUITE ERRORS", output, StringComparison.Ordinal);
    }

    // ── --jobs aggregate ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Under --jobs the parent reprints each shard's output and then its OWN aggregate, and
    /// that aggregate is the only complete summary a caller reads. It learns what a shard lost
    /// by counting Reporter's per-bundle headers in the captured text (#2715's mechanism). The
    /// SUITE ERRORS header was not one of them, so a shard that lost suites folded into the
    /// aggregate as a clean smaller total — #2715's own defect, one Stage over.
    /// </summary>
    [Fact]
    public void FanOut_CountsTheSuiteErrorHeaderInAShardsOutput()
    {
        const string shardOutput =
            "=== bundle-a — SUITE ERRORS (2) ===\n"
            + "  <bundled>: EMIT-EXCLUDED for X: 2 object(s) dropped\n"
            + "=== bundle-a ===\n"
            + "PASS  Codeunit1.T (1ms)\n"
            + "=== bundle-b — SUITE ERRORS (1) ===\n"
            + "  <bundled>: EMIT-ZERO (3 AL error(s))\n";

        Assert.Equal(2, Infrastructure.ParallelFanOut.CountOccurrences(
            shardOutput, Infrastructure.ParallelFanOut.PartialLossHeader));
    }

    /// <summary>
    /// The load-bearing negative. A bundle that lost suites still contributes its SURVIVING
    /// tests to the shard's JUnit, so counting it as "not run" would tell the caller a bundle
    /// is absent from totals it is actually in — a wrong number stated confidently, which is
    /// the failure mode this whole issue is about.
    /// </summary>
    [Fact]
    public void FanOut_SuiteErrorHeaderIsNotCountedAsANotRunBundle()
    {
        const string shardOutput =
            "=== bundle-a — SUITE ERRORS (1) ===\n"
            + "  <bundled>: EMIT-EXCLUDED for X: 1 object(s) dropped\n"
            + "=== bundle-a ===\n"
            + "PASS  Codeunit1.T (1ms)\n";

        var notRun = Infrastructure.ParallelFanOut.NotRunHeaders
            .Sum(h => Infrastructure.ParallelFanOut.CountOccurrences(shardOutput, h));

        Assert.Equal(0, notRun);
    }

    /// <summary>Negative: a clean shard's output contains neither marker.</summary>
    [Fact]
    public void FanOut_CleanShardOutput_CountsNothing()
    {
        const string shardOutput = "=== bundle-a ===\nPASS  Codeunit1.T (1ms)\n";

        Assert.Equal(0, Infrastructure.ParallelFanOut.CountOccurrences(
            shardOutput, Infrastructure.ParallelFanOut.PartialLossHeader));
        Assert.Equal(0, Infrastructure.ParallelFanOut.NotRunHeaders
            .Sum(h => Infrastructure.ParallelFanOut.CountOccurrences(shardOutput, h)));
    }

    // ── End to end, against the real runner ──────────────────────────────────────────

    private static (string Output, int Exit) RunRunner(params string[] extraArgs)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        foreach (var a in extraArgs) args.Append(' ').Append(a);
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(240_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    /// <summary>
    /// The literal #2762 reproduction. One object in `excluded-suite` cannot bind, so the whole
    /// module is dropped and BOTH its [Test] procedures vanish — the loss is larger than the
    /// excluded-object count, which is exactly why the count alone is not the story. The
    /// healthy sibling suite keeps the bucket at Stage=Ran.
    ///
    /// Before this change the run printed the EMIT-EXCLUDED line ~10 lines up and then a
    /// summary block identical to a clean 1-test run.
    /// </summary>
    [SkippableFact]
    public void EmitExcludedBundle_SummaryNamesTheLossBelowTheSummaryHeader()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner($"\"{PartialBundle}\"");

        // The sibling really ran — without this a runner that died on the whole bundle would
        // satisfy everything below while proving nothing.
        Assert.Contains("PartialBundleHealthy_StillRuns", output, StringComparison.Ordinal);
        Assert.Equal(3, exit);

        // The exclusion happened, and it is the EMIT-EXCLUDED path (not EMIT-ZERO).
        Assert.Contains("EMIT-EXCLUDED", output, StringComparison.Ordinal);

        // ...and the actual defect: it is stated in the summary, at the bottom, where the
        // reader is — not only on a stderr line above the results.
        var summaryStart = output.IndexOf("al-runner — test run summary", StringComparison.Ordinal);
        Assert.True(summaryStart >= 0, $"no summary block in output:\n{output}");
        var summary = output[summaryStart..];
        Assert.Contains("Suite errors: 1", summary, StringComparison.Ordinal);
        Assert.Contains("EMIT-EXCLUDED", summary, StringComparison.Ordinal);
        Assert.Contains("partial:     1", summary, StringComparison.Ordinal);

        // The counts still say what they measured.
        Assert.Contains("Tests:         1 total", summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// Negative, end to end: the healthy suite alone exits 0 and its output gains nothing. A
    /// fix that failed every bundle, or that printed the new section unconditionally, would
    /// pass the test above and fail here.
    /// </summary>
    [SkippableFact]
    public void HealthySuiteAlone_ExitsZeroWithNoSuiteErrorSection()
    {
        TestArtifacts.SkipIfMissing();

        var target = Path.Combine(PartialBundle, "healthy-suite");
        var (output, exit) = RunRunner($"\"{target}\"");

        Assert.Equal(0, exit);
        Assert.Contains("PartialBundleHealthy_StillRuns", output, StringComparison.Ordinal);
        Assert.DoesNotContain("EMIT-EXCLUDED", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Suite errors", output, StringComparison.Ordinal);
        Assert.DoesNotContain("partial:", output, StringComparison.Ordinal);
        Assert.Contains("Tests:         1 total", output, StringComparison.Ordinal);
    }
}
