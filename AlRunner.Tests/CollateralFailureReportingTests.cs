// CollateralFailureReportingTests — issues #2880 and #2746.
//
// #2762 / #2793 made a lost suite VISIBLE: the summary gains a `partial:` line and a
// "Suite errors" block, --output-json gains `suiteErrors`, --out gains a `"kind": "suite"`
// record, and --watch gains a SUITE ERRORS node. PartialSuiteLossReportingTests pins all of
// that and none of it changes here.
//
// What none of them said is what the loss did to the results that DID run. Measured on the
// real incident in #2880 — a runner-extras run where the consolidated standalone-suites module
// failed to compile, dropping 23 suites:
//
//     Tests:         187 total   pass: 180   fail: 7
//
// Every one of those 7 FAILs was collateral. They are in suites that survived, and they failed
// because objects they need were declared in the module that did not compile —
// Query777_RoleCenterFromPlans_NoMatchingPlan_ReturnsNoRows, JoinWith*,
// RightOuterJoin_IsOutOfScope_ThrowsNamedReason. Re-run on the identical tree and build, the
// same run was 265/265 green. Nothing in any reporting surface distinguished those 7 from a
// real regression, and the natural response to seven substantive-looking FAILs in query joins
// and role centres is to go and investigate query joins and role centres.
//
// So the claim under test is narrower than "the loss is named" (#2762's claim) and narrower
// than "the errors are printed" (#2746's): a fail/error result that shares a bucket with a
// suite error is REPORTED AS UNVERIFIED, with a count, wherever that result is reported.
//
// The negatives carry the weight. An implementation that marks every failure, or that prints
// the marker unconditionally, satisfies every positive below and is exactly as useless as
// marking none — so each surface has a control asserting a clean bucket's failures are NOT
// marked and a clean run's output is unchanged.
//
// #2746's half: the per-bundle progress line printed `N suite errors` as a bare number while
// the run was going, and nothing beside it said which suite or why. That line is emitted from
// Program.cs's run loop, minutes before any summary, and it is what a developer watching a
// long run actually reads.
//
// Entirely about the RUNNER's own reporting — "what does al-runner print when a bundle loses a
// suite" is not a question a BC service tier can adjudicate, so nothing here belongs in the
// al-language corpus (.claude/rules/bc-behavior-tests-go-upstream.md).

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public sealed class CollateralFailureReportingTests
{
    private const string SuiteError =
        "<bundled>: COMPILE-FAIL (24): _polyfill.cs(31,24): error CS0400: The type or namespace "
        + "name 'AlRunner' could not be found in the global namespace";

    private static TestResult Pass(string codeunit, string method) =>
        new(codeunit, method, TestOutcome.Pass, null, null, TimeSpan.FromMilliseconds(3));

    private static TestResult Fail(string codeunit, string method) =>
        new(codeunit, method, TestOutcome.Fail, "Assert.AreEqual failed", null,
            TimeSpan.FromMilliseconds(5));

    private static TestResult Error(string codeunit, string method) =>
        new(codeunit, method, TestOutcome.Error, "object not found", null,
            TimeSpan.FromMilliseconds(5));

    /// <summary>
    /// The #2880 shape: a bucket that ran, lost a suite, and whose surviving tests include
    /// failures. One pass and one fail so a marker cannot be confused with "the bucket failed".
    /// </summary>
    private static BucketResult PartialBucketWithFailures(string path = "/runner-extras") =>
        new(path, BucketStage.Ran,
            new[] { SuiteError }, null,
            new[]
            {
                Pass("Codeunit60100", "Healthy_StillPasses"),
                Fail("Codeunit64535", "JoinWithLeftOuterJoin_ReturnsRows"),
                Error("Codeunit60391", "RightOuterJoin_IsOutOfScope_ThrowsNamedReason"),
            },
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1, null);

    /// <summary>The control: the identical results with nothing lost.</summary>
    private static BucketResult CleanBucketWithFailures(string path = "/runner-extras") =>
        new(path, BucketStage.Ran,
            Array.Empty<string>(), null,
            new[]
            {
                Pass("Codeunit60100", "Healthy_StillPasses"),
                Fail("Codeunit64535", "JoinWithLeftOuterJoin_ReturnsRows"),
                Error("Codeunit60391", "RightOuterJoin_IsOutOfScope_ThrowsNamedReason"),
            },
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

    // ── Summary: the count ───────────────────────────────────────────────────────────

    /// <summary>
    /// The one number the #2880 log did not have. `fail: 7` next to `partial: 1` leaves the
    /// reader to work out whether the 7 are related to the 1; this states it.
    /// </summary>
    [Fact]
    public void Summary_FailuresInABucketThatLostASuite_AreCountedAsSuspect()
    {
        var summary = Summarize(PartialBucketWithFailures());

        // 1 fail + 1 error, both in the partial bucket.
        Assert.Contains("suspect:     2", summary, StringComparison.Ordinal);
        // …and the reason, naming how many suites were lost, in the same place.
        Assert.Contains("lost 1 suite(s)", summary, StringComparison.Ordinal);
        Assert.Contains("UNVERIFIED", summary, StringComparison.Ordinal);

        // The real counts are untouched — a marker that changed the totals would be a second
        // wrong number, not a fix.
        Assert.Contains("Tests:         3 total", summary, StringComparison.Ordinal);
        Assert.Contains("pass:        1", summary, StringComparison.Ordinal);
        Assert.Contains("fail:        1", summary, StringComparison.Ordinal);
        Assert.Contains("error:       1", summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The load-bearing negative. Without it, an implementation that prints `suspect:` on every
    /// run — or that counts every failure as suspect — passes the test above and destroys the
    /// distinction it exists to create.
    /// </summary>
    [Fact]
    public void Summary_FailuresInACleanBucket_AreNotSuspect()
    {
        var summary = Summarize(CleanBucketWithFailures());

        Assert.DoesNotContain("suspect:", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("UNVERIFIED", summary, StringComparison.Ordinal);
        Assert.Contains("fail:        1", summary, StringComparison.Ordinal);
        Assert.Contains("error:       1", summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// A run with BOTH a partial bucket and an intact one: only the partial bucket's failures
    /// are suspect. An implementation that marks the whole run once a single bucket is partial
    /// would report 4 here and tell the reader to distrust two results that nothing threatens.
    /// </summary>
    [Fact]
    public void Summary_OnlyTheFailuresSharingABucketWithASuiteErrorAreCounted()
    {
        var summary = Summarize(PartialBucketWithFailures("/partial"),
                                CleanBucketWithFailures("/intact"));

        Assert.Contains("suspect:     2", summary, StringComparison.Ordinal);
        Assert.Contains("fail:        2", summary, StringComparison.Ordinal);
        Assert.Contains("error:       2", summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// A partial bucket whose survivors all PASSED has nothing to mark. Printing `suspect: 0`
    /// there would train the reader to skip the line on the runs where it matters.
    /// </summary>
    [Fact]
    public void Summary_PartialBucketWithNoFailures_PrintsNoSuspectLine()
    {
        var bucket = new BucketResult("/runner-extras", BucketStage.Ran,
            new[] { SuiteError }, null, new[] { Pass("Codeunit60100", "Healthy_StillPasses") },
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1, null);

        var summary = Summarize(bucket);

        Assert.DoesNotContain("suspect:", summary, StringComparison.Ordinal);
        // The loss itself is still reported — this must not weaken #2762.
        Assert.Contains("Suite errors: 1", summary, StringComparison.Ordinal);
    }

    // ── Per-test report: the marker next to the result ───────────────────────────────

    /// <summary>
    /// The count in the summary is 60 lines below the FAIL line an agent reads first. The
    /// marker has to be ON the result.
    /// </summary>
    [Fact]
    public void PerTest_FailuresInAPartialBucket_AreMarkedSuspectOnTheirOwnLine()
    {
        var report = PerTest(showPass: false, PartialBucketWithFailures());

        var failLine = report.Split('\n')
            .Single(l => l.Contains("JoinWithLeftOuterJoin_ReturnsRows", StringComparison.Ordinal)
                      && l.StartsWith("FAIL", StringComparison.Ordinal));
        Assert.Contains("[suspect", failLine, StringComparison.Ordinal);
        Assert.Contains("lost 1 suite(s)", failLine, StringComparison.Ordinal);

        var errLine = report.Split('\n')
            .Single(l => l.Contains("RightOuterJoin_IsOutOfScope_ThrowsNamedReason", StringComparison.Ordinal)
                      && l.StartsWith("ERROR", StringComparison.Ordinal));
        Assert.Contains("[suspect", errLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// The passing sibling in the same bucket is NOT marked. A pass is not made wrong by a
    /// missing object — it is the failures that a missing object manufactures.
    /// </summary>
    [Fact]
    public void PerTest_PassesInAPartialBucket_AreNotMarked()
    {
        var report = PerTest(showPass: true, PartialBucketWithFailures());

        var passLine = report.Split('\n')
            .Single(l => l.Contains("Healthy_StillPasses", StringComparison.Ordinal)
                      && l.StartsWith("PASS", StringComparison.Ordinal));
        Assert.DoesNotContain("[suspect", passLine, StringComparison.Ordinal);
    }

    /// <summary>Negative: an intact bucket's failures carry no marker at all.</summary>
    [Fact]
    public void PerTest_FailuresInACleanBucket_AreNotMarked()
    {
        var report = PerTest(showPass: false, CleanBucketWithFailures());

        Assert.DoesNotContain("[suspect", report, StringComparison.Ordinal);
        Assert.Contains("JoinWithLeftOuterJoin_ReturnsRows", report, StringComparison.Ordinal);
    }

    // ── Failures-by-classification: the "where to attack next" view ──────────────────

    /// <summary>
    /// The most dangerous surface in #2880: this view ranks failure clusters and reads as a work
    /// list. Seven collateral failures clustered into three plausible-looking areas is precisely
    /// the seven ghost issues the incident would have produced.
    /// </summary>
    [Fact]
    public void FailureClassification_WhenFailuresAreSuspect_SaysSoBeforeTheRanking()
    {
        var w = new StringWriter();
        Reporter.PrintFailureClassification(new[] { PartialBucketWithFailures() }, w);
        var text = w.ToString();

        Assert.Contains("Failures by classification", text, StringComparison.Ordinal);
        Assert.Contains("2 of 2", text, StringComparison.Ordinal);
        Assert.Contains("suspect", text, StringComparison.Ordinal);
    }

    /// <summary>Negative: an intact run's classification view is unchanged.</summary>
    [Fact]
    public void FailureClassification_CleanBucket_HasNoSuspectNote()
    {
        var w = new StringWriter();
        Reporter.PrintFailureClassification(new[] { CleanBucketWithFailures() }, w);
        var text = w.ToString();

        Assert.Contains("Failures by classification", text, StringComparison.Ordinal);
        Assert.DoesNotContain("suspect", text, StringComparison.Ordinal);
    }

    // ── --output-json ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A scripted consumer reads this document, not the console. Without a per-test field it
    /// has to correlate `suiteErrors` against `tests` by bucket — which the document does not
    /// even give it, since `tests` is flattened across buckets.
    /// </summary>
    [Fact]
    public void Json_FailingTestsInAPartialBucket_CarrySuspectTrue()
    {
        var json = Reporter.SerializeJsonOutput(new[] { PartialBucketWithFailures() }, exitCode: 3);
        using var doc = JsonDocument.Parse(json);

        var tests = doc.RootElement.GetProperty("tests").EnumerateArray().ToList();
        var failed = tests.Single(t => t.GetProperty("name").GetString()!
            .EndsWith("JoinWithLeftOuterJoin_ReturnsRows", StringComparison.Ordinal));
        Assert.True(failed.GetProperty("suspect").GetBoolean());

        // The pass in the same bucket has no such field at all — null-omitted, so a consumer
        // reading `suspect` gets a true or nothing, never a misleading false on a green result.
        var passed = tests.Single(t => t.GetProperty("name").GetString()!
            .EndsWith("Healthy_StillPasses", StringComparison.Ordinal));
        Assert.False(passed.TryGetProperty("suspect", out _));
    }

    /// <summary>Negative: a clean run serialises byte-identically to before.</summary>
    [Fact]
    public void Json_CleanBucket_HasNoSuspectFieldOnAnyTest()
    {
        var json = Reporter.SerializeJsonOutput(new[] { CleanBucketWithFailures() }, exitCode: 1);

        Assert.DoesNotContain("suspect", json, StringComparison.Ordinal);
    }

    // ── --out classification file ────────────────────────────────────────────────────

    /// <summary>
    /// The triage file is what a follow-up pass reads to decide what to work on. A collateral
    /// failure in it is a work item nobody should start.
    /// </summary>
    [Fact]
    public void Classification_FailureRecordsInAPartialBucket_AreMarkedSuspect()
    {
        var path = TestScratch.FilePath("al-runner-cls", "classification.json");
        try
        {
            Reporter.WriteClassification(new[] { PartialBucketWithFailures() }, path);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));

            var all = doc.RootElement.GetProperty("all_failures").EnumerateArray().ToList();
            var testFailure = all.Single(f => f.TryGetProperty("method", out var m)
                && m.GetString() == "JoinWithLeftOuterJoin_ReturnsRows");
            Assert.True(testFailure.GetProperty("suspect").GetBoolean());

            // The suite-error record itself is not "suspect" — it IS the cause, and calling it
            // unverified would point the reader away from the only real problem in the run.
            var suiteRecord = all.Single(f => f.GetProperty("kind").GetString() == "suite");
            Assert.False(suiteRecord.TryGetProperty("suspect", out _));

            // The passing sibling contributes no failure record at all, so nothing marks it.
            Assert.DoesNotContain(all, f => f.TryGetProperty("method", out var m)
                && m.GetString() == "Healthy_StillPasses");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>
    /// Negative: an intact bucket's failure records say `suspect: false` — present, so a
    /// consumer can filter on it, and false, so it does not spread doubt over a real failure.
    /// This document does not suppress nulls, which is why the field is a plain bool here and
    /// a null-omitted one in --output-json.
    /// </summary>
    [Fact]
    public void Classification_CleanBucket_MarksNothingSuspect()
    {
        var path = TestScratch.FilePath("al-runner-cls", "classification.json");
        try
        {
            Reporter.WriteClassification(new[] { CleanBucketWithFailures() }, path);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));

            var all = doc.RootElement.GetProperty("all_failures").EnumerateArray().ToList();
            Assert.Equal(2, all.Count);
            Assert.All(all, f => Assert.False(f.GetProperty("suspect").GetBoolean()));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── --watch dashboard ────────────────────────────────────────────────────────────

    private static string RenderWatch(params BucketResult[] results)
    {
        var console = new Spectre.Console.Testing.TestConsole();
        console.Profile.Width = 200;
        console.Write(WatchDashboard.Build(results, "runner-extras", WatchStatus.Idle,
            new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Local), TimeSpan.FromSeconds(1)));
        return console.Output;
    }

    /// <summary>
    /// --watch has no exit code and no summary a reader scrolls to; the tree is the whole
    /// verdict. A red FAIL node next to a red SUITE ERRORS node reads as two problems.
    /// </summary>
    [Fact]
    public void WatchTree_FailingTestInAPartialBucket_IsMarkedSuspect()
    {
        var output = RenderWatch(PartialBucketWithFailures("/tmp/runner-extras"));

        Assert.Contains("SUITE ERRORS (1)", output, StringComparison.Ordinal);
        Assert.Contains("JoinWithLeftOuterJoin_ReturnsRows", output, StringComparison.Ordinal);
        Assert.Contains("suspect", output, StringComparison.Ordinal);
    }

    /// <summary>Negative: an intact cycle's tree gains nothing.</summary>
    [Fact]
    public void WatchTree_CleanBucket_MarksNoTestSuspect()
    {
        var output = RenderWatch(CleanBucketWithFailures("/tmp/runner-extras"));

        Assert.DoesNotContain("suspect", output, StringComparison.Ordinal);
        Assert.Contains("JoinWithLeftOuterJoin_ReturnsRows", output, StringComparison.Ordinal);
    }

    // ── #2746: the per-bundle progress line ──────────────────────────────────────────

    /// <summary>
    /// `→ 225P/0F/0E across 225 tests, 1 suite errors (30.9s)` is what the run prints while it
    /// is running. #2746's whole complaint is that the "1" there has no name attached and
    /// nothing beside it ever gives it one.
    /// </summary>
    [Fact]
    public void BundleProgress_WithSuiteErrors_NamesThemUnderTheCountLine()
    {
        var lines = Infrastructure.BundleProgressLine.Render(
            pass: 225, fail: 0, error: 0, tests: 225,
            suiteErrors: new[] { SuiteError },
            elapsed: TimeSpan.FromSeconds(30.9)).ToList();

        Assert.Equal("  → 225P/0F/0E across 225 tests, 1 suite errors (30.9s)", lines[0]);
        Assert.Contains(SuiteError, lines[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// Negative: without suite errors the line is byte-identical to what Program.cs printed
    /// before, and there is nothing after it. Every integration test asserting on this line
    /// depends on that.
    /// </summary>
    [Fact]
    public void BundleProgress_WithoutSuiteErrors_IsExactlyTheOldSingleLine()
    {
        var lines = Infrastructure.BundleProgressLine.Render(
            pass: 265, fail: 0, error: 0, tests: 265,
            suiteErrors: Array.Empty<string>(),
            elapsed: TimeSpan.FromSeconds(42.1)).ToList();

        Assert.Single(lines);
        Assert.Equal("  → 265P/0F/0E across 265 tests, 0 suite errors (42.1s)", lines[0]);
    }

    /// <summary>
    /// A bundle that lost 23 suites must not print 23 screens of text into the middle of a
    /// running log. Capped, and the cap says how many it hid — a silent truncation would be a
    /// new instance of the defect this issue is about.
    /// </summary>
    [Fact]
    public void BundleProgress_ManySuiteErrors_IsCappedAndSaysHowManyItHid()
    {
        var errors = Enumerable.Range(1, 24).Select(i => $"<bundled>: COMPILE-FAIL ({i})").ToArray();

        var lines = Infrastructure.BundleProgressLine.Render(
            pass: 180, fail: 7, error: 0, tests: 187,
            suiteErrors: errors, elapsed: TimeSpan.FromSeconds(60)).ToList();

        Assert.Contains("24 suite errors", lines[0], StringComparison.Ordinal);
        Assert.Equal(1 + Infrastructure.BundleProgressLine.MaxInlineErrors + 1, lines.Count);
        Assert.Contains($"and {24 - Infrastructure.BundleProgressLine.MaxInlineErrors} more",
            lines[^1], StringComparison.Ordinal);
    }

    // ── The note is only printed when there is something below it to mark ────────────
    //
    // Review of #2898 found both notes sitting inside `if (b.CompileErrors.Count > 0)` and
    // ungated on whether anything in the bucket is actually suspect. A partial bucket whose
    // survivors all passed announced "FAIL/ERROR results below are marked …" and then printed
    // no results at all — `visible.Count == 0` skips the section on the very next line. The
    // summary already got this right (Summary_PartialBucketWithNoFailures_PrintsNoSuspectLine);
    // these hold the other two surfaces to the same standard.

    private static BucketResult PartialBucketAllSurvivorsPassed(
        string path = "/runner-extras", int lostSuites = 1)
    {
        var errors = Enumerable.Range(1, lostSuites)
            .Select(i => $"<bundled>: COMPILE-FAIL ({i}): suite {i} did not compile").ToArray();
        return new BucketResult(path, BucketStage.Ran, errors, null,
            new[] { Pass("Codeunit60100", "Healthy_StillPasses"),
                    Pass("Codeunit60101", "AlsoHealthy_StillPasses") },
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1, null);
    }

    [Fact]
    public void PerTest_PartialBucketWhoseSurvivorsAllPassed_PrintsNoSuspectNote()
    {
        var report = PerTest(showPass: false, PartialBucketAllSurvivorsPassed());

        // #2762's half is untouched: the loss is still named, loudly.
        Assert.Contains("SUITE ERRORS (1)", report, StringComparison.Ordinal);
        Assert.Contains("MISSING from this run", report, StringComparison.Ordinal);
        // …but there is nothing below to mark, so nothing claims there is.
        Assert.DoesNotContain("marked [suspect", report, StringComparison.Ordinal);
    }

    [Fact]
    public void WatchTree_PartialBucketWhoseSurvivorsAllPassed_PrintsNoSuspectNote()
    {
        var output = RenderWatch(PartialBucketAllSurvivorsPassed("/tmp/runner-extras"));

        Assert.Contains("SUITE ERRORS (1)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("suspect", output, StringComparison.Ordinal);
    }

    // ── One marker, one number ───────────────────────────────────────────────────────
    //
    // The summary used to quote the RUN-WIDE lost-suite total while every per-result marker
    // quoted its own bucket's. With one bucket losing 23 suites and passing everything, and
    // another losing 1 and failing twice, the summary said "lost 24 suite(s)" and every marked
    // line said "bucket lost 1 suite(s)" — the same two-numbers-one-marker defect the single
    // IsSuspect predicate exists to prevent, one level up. Summary_OnlyTheFailuresSharing…
    // could not catch it: its second bucket is clean, so the two numbers coincide.

    [Fact]
    public void Summary_SuspectLine_QuotesOnlyTheSuitesLostByBucketsThatContributedSuspects()
    {
        var summary = Summarize(
            PartialBucketAllSurvivorsPassed("/lost-23-but-all-green", lostSuites: 23),
            PartialBucketWithFailures("/lost-1-and-failed"));

        Assert.Contains("suspect:     2", summary, StringComparison.Ordinal);
        // Only the bucket that produced those 2 is quoted…
        Assert.Contains("that also lost 1 suite(s).", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("that also lost 24 suite(s)", summary, StringComparison.Ordinal);
        // …and the run-wide total is still reported where it belongs, on the `partial:` line.
        Assert.Contains("partial:     2  (ran, but 24 suite(s) did not", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_SuspectLineNumber_MatchesThePerResultMarker()
    {
        var buckets = new[] { PartialBucketAllSurvivorsPassed("/lost-23-but-all-green", lostSuites: 23),
                              PartialBucketWithFailures("/lost-1-and-failed") };

        var summary = Summarize(buckets);
        var perTest = PerTest(false, buckets);

        var summaryCount = System.Text.RegularExpressions.Regex
            .Match(summary, @"that also lost (\d+) suite\(s\)").Groups[1].Value;
        var markerCounts = System.Text.RegularExpressions.Regex
            .Matches(perTest, @"bucket lost (\d+) suite\(s\)")
            .Select(m => m.Groups[1].Value).Distinct().ToList();

        Assert.Equal("1", summaryCount);
        Assert.Equal(new[] { "1" }, markerCounts);
    }

    // ── The cause of a suite error is not its own collateral ─────────────────────────
    //
    // In general the runner cannot know which failure caused a suite error. In the watchdog
    // case it can: the abort reason names the codeunit and method that hung, and that result
    // is the one thing in the run that is definitely real. Marking it "unverified" points the
    // reader away from the only genuine problem — the same argument that already keeps the
    // `"kind": "suite"` record in --out unmarked.

    /// <summary>A real abort line, built by the executor's own formatter so the two cannot drift.</summary>
    private static string AbortError(string codeunit, string method) =>
        "runner-extras/timeout: " + Infrastructure.BundleFailureStage.TestTimeoutAbort + ": "
        + Infrastructure.BundleFailureStage.AbortReasonHead($"Codeunit 62212 \"Hang Suite\"", codeunit, method)
        + " — 2 further [Test] method(s) in this codeunit did not run (2 total)";

    private static BucketResult AbortedBucket(string path = "/runner-extras") =>
        new(path, BucketStage.Ran,
            new[] { AbortError("Codeunit62212", "Hangs") }, null,
            new[]
            {
                Pass("Codeunit62212", "Fine"),
                Error("Codeunit62212", "Hangs"),
                Fail("Codeunit60100", "Unrelated_Fails"),
            },
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1, null);

    [Fact]
    public void PerTest_TheTestNamedByAWatchdogAbort_IsNotMarkedSuspect()
    {
        var report = PerTest(showPass: false, AbortedBucket());

        var hung = report.Split('\n').Single(l => l.StartsWith("ERROR", StringComparison.Ordinal)
            && l.Contains("Codeunit62212.Hangs", StringComparison.Ordinal));
        Assert.DoesNotContain("[suspect", hung, StringComparison.Ordinal);

        // The control in the same bucket: an unrelated failure IS still collateral, so an
        // implementation that simply stopped marking errors fails here.
        var other = report.Split('\n').Single(l => l.StartsWith("FAIL", StringComparison.Ordinal)
            && l.Contains("Unrelated_Fails", StringComparison.Ordinal));
        Assert.Contains("[suspect", other, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_TheTestNamedByAWatchdogAbort_IsNotCounted()
    {
        var summary = Summarize(AbortedBucket());

        // 1 fail + 1 error in the bucket, but the error is the named cause.
        Assert.Contains("suspect:     1", summary, StringComparison.Ordinal);
        Assert.Contains("fail:        1", summary, StringComparison.Ordinal);
        Assert.Contains("error:       1", summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The negative control for the exclusion. A COMPILE-FAIL suite error names no test, so the
    /// runner cannot tell cause from collateral and marks both — an implementation that excluded
    /// every error, or matched on the outcome rather than on the named test, fails here.
    /// </summary>
    [Fact]
    public void PerTest_WhenTheSuiteErrorNamesNoTest_EveryFailureIsStillMarked()
    {
        var bucket = new BucketResult("/runner-extras", BucketStage.Ran,
            new[] { SuiteError }, null,
            new[]
            {
                Pass("Codeunit62212", "Fine"),
                Error("Codeunit62212", "Hangs"),
                Fail("Codeunit60100", "Unrelated_Fails"),
            },
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1, null);

        var report = PerTest(showPass: false, bucket);

        Assert.Equal(2, report.Split('\n').Count(l => l.Contains("[suspect", StringComparison.Ordinal)
            && (l.StartsWith("FAIL", StringComparison.Ordinal) || l.StartsWith("ERROR", StringComparison.Ordinal))));
    }

    /// <summary>
    /// A different test in the SAME codeunit as the hung one is still collateral: the watchdog
    /// abandons the rest of the codeunit, so anything else attributed to it is exactly the kind
    /// of result the marker exists for. Pins that the match is on the test, not the codeunit.
    /// </summary>
    [Fact]
    public void PerTest_ASiblingMethodOfTheHungTest_IsStillMarked()
    {
        var bucket = new BucketResult("/runner-extras", BucketStage.Ran,
            new[] { AbortError("Codeunit62212", "Hangs") }, null,
            new[] { Error("Codeunit62212", "Hangs"), Fail("Codeunit62212", "HangsToo") },
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1, null);

        var report = PerTest(showPass: false, bucket);

        var sibling = report.Split('\n').Single(l => l.Contains("HangsToo", StringComparison.Ordinal));
        Assert.Contains("[suspect", sibling, StringComparison.Ordinal);
    }

    /// <summary>
    /// The round trip. TestExecutor formats the abort reason and Reporter reads it back; if the
    /// wording changes on one side only, the exclusion silently stops applying and every aborted
    /// suite goes back to marking its own cause. This is what makes the string match safe.
    /// </summary>
    [Fact]
    public void AbortReasonHead_IsRecognisedAsNamingTheTestItWasBuiltFor()
    {
        var line = "app-group: " + Infrastructure.BundleFailureStage.TestTimeoutAbort + ": "
            + Infrastructure.BundleFailureStage.AbortReasonHead("Codeunit 50000 \"X\"", "Codeunit50000", "Slow")
            + " — 3 further [Test] method(s) in this codeunit did not run (3 total)";

        Assert.True(Infrastructure.BundleFailureStage.AbortReasonNamesTest(line, "Codeunit50000", "Slow"));
        Assert.False(Infrastructure.BundleFailureStage.AbortReasonNamesTest(line, "Codeunit50000", "Fast"));
        Assert.False(Infrastructure.BundleFailureStage.AbortReasonNamesTest(line, "Codeunit50001", "Slow"));
        // A line without the marker never names a test, whatever it happens to contain.
        Assert.False(Infrastructure.BundleFailureStage.AbortReasonNamesTest(
            "app-group: COMPILE-FAIL: (Codeunit50000).Slow: watchdog timeout aborted the run",
            "Codeunit50000", "Slow"));
    }

    /// <summary>
    /// The sibling parser, asked the same question. AbortResumePlan reads the SAME reason string
    /// with its own regex to decide which codeunits a watchdog resume must skip — it predates the
    /// shared formatter and still carries its own copy of the wording. Pinned against the
    /// formatter here so a reword breaks a test rather than silently disabling BOTH the resume
    /// and the cause-exclusion above, each in a way nothing else would report.
    /// </summary>
    [Fact]
    public void AbortReasonHead_IsAlsoParsedByTheWatchdogResumePlanner()
    {
        var reason = Infrastructure.BundleFailureStage.AbortReasonHead(
                "Codeunit 62212 \"Hang Suite\"", "Codeunit62212", "Hangs")
            + " — 2 further [Test] method(s) in this codeunit did not run (2 total)";

        Assert.Contains("Codeunit62212",
            Infrastructure.AbortResumePlan.NextExclusions(new[] { reason }, Array.Empty<string>()));
    }

}
