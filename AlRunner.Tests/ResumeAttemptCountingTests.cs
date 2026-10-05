// ResumeAttemptCountingTests — the pure parts of a watchdog resume (#2280) reporting each bundle and
// each test once however many attempts ran: #5268 (a dropped codeunit's SKIPPED rows) and #5269
// (the aggregate's per-bundle headers). The end-to-end halves spawn real runners:
// ResumeEmitExcludedPlainTests and ResumeEmitExcludedJobsTests.

using System.Globalization;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class ResumeAttemptCountingTests : IDisposable
{
    private readonly string _dir = TestScratch.FlatDir("resumeattempt-");

    public ResumeAttemptCountingTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static TestResult Row(string codeunit, string method, TestOutcome outcome = TestOutcome.Skipped) =>
        new(codeunit, method, outcome, null, null, TimeSpan.Zero);

    private static BucketResult Bucket(string path, params TestResult[] tests) =>
        new(path, BucketStage.Ran, Array.Empty<string>(), null, tests,
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1, null);

    // ── #5268: rows a carried attempt already reported ───────────────────────────────────────

    [Fact]
    public void NotYetReported_DropsTheRowsTheCarryHolds_AndKeepsTheOthers()
    {
        var carried = new[] { Bucket("/b", Row("Dropped", "A"), Row("Dropped", "B")) };
        var rows = new[] { Row("Dropped", "A"), Row("Dropped", "B"), Row("Dropped", "C") };

        var left = ResumeCarry.NotYetReported(carried, "/b", rows);

        Assert.Equal(new[] { "C" }, left.Select(r => r.Method));
    }

    /// <summary>The carry's rows belong to their own bundle: a codeunit of the same name and method in
    /// another bundle is a different test, and is still owed.</summary>
    [Fact]
    public void NotYetReported_ReadsOnlyTheSameBundlesCarriedRows()
    {
        var carried = new[] { Bucket("/other", Row("Dropped", "A")) };

        var left = ResumeCarry.NotYetReported(carried, "/b", new[] { Row("Dropped", "A") });

        Assert.Single(left);
    }

    /// <summary>Both halves of the key: the same method name in another codeunit, and another method of
    /// the same codeunit, are not reported.</summary>
    [Fact]
    public void NotYetReported_MatchesOnCodeunitAndMethodTogether()
    {
        var carried = new[] { Bucket("/b", Row("Dropped", "A")) };

        var left = ResumeCarry.NotYetReported(carried, "/b",
            new[] { Row("Dropped", "A"), Row("Dropped", "B"), Row("Other", "A") });

        Assert.Equal(new[] { "Dropped.B", "Other.A" }, left.Select(r => $"{r.Codeunit}.{r.Method}").OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void NotYetReported_WithNothingCarried_ReturnsEveryRow()
    {
        var rows = new[] { Row("Dropped", "A"), Row("Dropped", "B") };

        Assert.Equal(2, ResumeCarry.NotYetReported(Array.Empty<BucketResult>(), "/b", rows).Count);
    }

    // ── #5268: the carried skipped count ─────────────────────────────────────────────────────

    private string JUnit(string name, int tests, int failures, int errors, int skipped)
    {
        var path = Path.Combine(_dir, name + ".xml");
        File.WriteAllText(path, string.Create(CultureInfo.InvariantCulture,
            $"<testsuites><testsuite name=\"s\" tests=\"{tests}\" failures=\"{failures}\" errors=\"{errors}\" skipped=\"{skipped}\"/></testsuites>"));
        return path;
    }

    /// <summary>A carried JUnit's skipped cases are carried as skipped: the printed summary used to take
    /// them into its total and into no category.</summary>
    [Fact]
    public void CarriedFromEarlierAttempts_CarriesTheSkippedCases()
    {
        var carried = ProgramSupport.CarriedFromEarlierAttempts(new[] { JUnit("a", 5, 0, 1, 3), JUnit("b", 2, 1, 0, 0) });

        Assert.Equal(new Reporter.CarriedTotals(7, 2, 1, 1, 3), carried);
    }

    [Fact]
    public void PrintSummary_AddsTheCarriedSkippedToItsCategories_SoTheyAddUpToTheTotal()
    {
        var w = new StringWriter();
        Reporter.PrintSummary(new[] { Bucket("/b", Row("Healthy", "A", TestOutcome.Pass)) }, w,
            new Reporter.CarriedTotals(5, 1, 0, 1, 3));

        var text = w.ToString();
        Assert.Matches(@"^Tests: 6   passed 2   failed 0   errors 1   skipped 3 ", text.Replace("\r", "").Substring(text.IndexOf("Tests:", StringComparison.Ordinal)));
        Assert.Contains("(carried from earlier attempt(s): 5 tests, 1 pass, 0 fail, 1 error, 3 skipped)", text);
    }

    /// <summary>Nothing skipped in the carry: both lines read exactly as they did, for the readers of them.</summary>
    [Fact]
    public void PrintSummary_WithNothingCarriedSkipped_KeepsItsLinesAsTheyWere()
    {
        var w = new StringWriter();
        Reporter.PrintSummary(new[] { Bucket("/b", Row("Healthy", "A", TestOutcome.Pass)) }, w,
            new Reporter.CarriedTotals(2, 1, 0, 1));

        var text = w.ToString();
        Assert.Contains("Tests: 3   passed 2   failed 0   errors 1        Time:", text);
        Assert.Contains("(carried from earlier attempt(s): 2 tests, 1 pass, 0 fail, 1 error)", text);
        Assert.DoesNotContain("skipped", text);
    }

    // ── #5269: the aggregate counts bundles, not the headers each attempt prints ─────────────

    private const string Errors = " — SUITE ERRORS (";
    private const string CompileFail = " — COMPILE FAIL ===";
    private const string SummaryLine = "Tests: 3   passed 1   failed 0   errors 1        Time: 1.0 s (wall 2.0 s)";

    private static string Attempt(params string[] lines) => string.Join("\n", lines) + "\n" + SummaryLine + "\n";

    private static string Suite(string label, int n = 1) => $"=== {label} — SUITE ERRORS ({n}) ===";

    [Fact]
    public void CountBundleHeaders_OneAttempt_CountsEachBundle()
    {
        var output = Attempt(Suite("a"), Suite("b", 2));

        Assert.Equal(2, ParallelFanOut.CountBundleHeaders(output, Errors, resumed: true));
        Assert.Equal(2, ParallelFanOut.CountBundleHeaders(output, Errors, resumed: false));
    }

    /// <summary>The shape of #5269: the worker resumed, and the bundle lost a suite in both attempts.</summary>
    [Fact]
    public void CountBundleHeaders_ABundleReportedByEveryAttempt_IsOneBundle()
    {
        var output = Attempt(Suite("a", 2)) + "resume: x\n" + Attempt(Suite("a"));

        Assert.Equal(1, ParallelFanOut.CountBundleHeaders(output, Errors, resumed: true));
    }

    /// <summary>The sibling shape: COMPILE FAIL is counted the same way.</summary>
    [Fact]
    public void CountBundleHeaders_ACompileFailReportedByEveryAttempt_IsOneBundle()
    {
        var output = Attempt("=== a — COMPILE FAIL ===") + Attempt("=== a — COMPILE FAIL ===");

        Assert.Equal(1, ParallelFanOut.CountBundleHeaders(output, CompileFail, resumed: true));
    }

    /// <summary>A bundle that reported in one attempt only (a watchdog abort is not repeated) still counts,
    /// and a different bundle in the next attempt is a second one: the attempts are not collapsed.</summary>
    [Fact]
    public void CountBundleHeaders_DifferentBundlesInDifferentAttempts_AreDifferentBundles()
    {
        var output = Attempt(Suite("a")) + Attempt(Suite("b"));

        Assert.Equal(2, ParallelFanOut.CountBundleHeaders(output, Errors, resumed: true));
    }

    /// <summary>"The attempt that printed it most": counts 1, 3, 2 of one label are 3, which is neither the
    /// first attempt's nor the last's.</summary>
    [Fact]
    public void CountBundleHeaders_TakesTheAttemptThatPrintedTheLabelMost_NotTheFirstOrTheLast()
    {
        var output = Attempt(Suite("t")) + Attempt(Suite("t"), Suite("t"), Suite("t")) + Attempt(Suite("t"), Suite("t"));

        Assert.Equal(3, ParallelFanOut.CountBundleHeaders(output, Errors, resumed: true));
    }

    /// <summary>Two bundles named `test` in one worker; the first attempt lost both (one only to the
    /// watchdog), the resumed one only the bundle with the drop: two bundles, though the last attempt says one.</summary>
    [Fact]
    public void CountBundleHeaders_TwoSameLabelBundlesWhereOnlyTheEarlierAttemptSawBoth_AreTwo()
    {
        var output = Attempt(Suite("test", 2), Suite("test")) + Attempt(Suite("test"));

        Assert.Equal(2, ParallelFanOut.CountBundleHeaders(output, Errors, resumed: true));
    }

    /// <summary>Two bundles that share a label in one attempt are two bundles; only a repeat across
    /// attempts is taken back.</summary>
    [Fact]
    public void CountBundleHeaders_TwoBundlesSharingALabelInOneAttempt_AreTwo()
    {
        var output = Attempt(Suite("tests"), Suite("tests")) + Attempt(Suite("tests"), Suite("tests"));

        Assert.Equal(2, ParallelFanOut.CountBundleHeaders(output, Errors, resumed: true));
    }

    /// <summary>A worker that died before it printed a summary line has no attempts to separate.</summary>
    [Fact]
    public void CountBundleHeaders_WithNoSummaryLine_CountsTheWholeOutputAsOneAttempt()
    {
        Assert.Equal(2, ParallelFanOut.CountBundleHeaders(Suite("a") + "\n" + Suite("b") + "\n", Errors, resumed: true));
        Assert.Equal(0, ParallelFanOut.CountBundleHeaders("", Errors, resumed: true));
    }

    // The line a failing test's message can forge: the runner prints message lines at column 0.
    private const string Forged = "Tests: 3   passed 1   failed 0   errors 1        Time: 1.0 s (wall 2.0 s)";

    /// <summary>A worker that did not resume is one attempt, so a summary line inside a test's message between
    /// two same-label bundles cannot fold them into one: both are counted, as before this change (#2715).</summary>
    [Fact]
    public void CountLostBundles_AWorkerThatDidNotResume_IsNeverCutAtAForgedSummaryLine()
    {
        var stdout = Suite("test") + "\n" + Forged + "\n" + Suite("test") + "\n" + SummaryLine + "\n";

        Assert.Equal(2, ParallelFanOut.CountLostBundles(stdout, "[log] nothing resumed\n").Partial);
    }

    /// <summary>The same for the bundles that did not run at all.</summary>
    [Fact]
    public void CountLostBundles_ForgedSummaryLineBetweenTwoSameLabelCompileFails_CountsBoth()
    {
        var header = "=== test — COMPILE FAIL ===";
        var stdout = header + "\n" + Forged + "\n" + header + "\n" + SummaryLine + "\n";

        Assert.Equal(2, ParallelFanOut.CountLostBundles(stdout, "").NotRun);
    }

    /// <summary>A worker that resumed is cut, and its repeated bundles are one each: the stderr notice is what says so.</summary>
    [Fact]
    public void CountLostBundles_AWorkerThatResumed_CountsABundleOncePerAttempt()
    {
        var stderr = AbortResume.AttemptEndedNotice + ". Continuing in a fresh process\n";
        var stdout = Attempt(Suite("a", 2), "=== c — COMPILE FAIL ===") + Attempt(Suite("a"), "=== c — COMPILE FAIL ===");

        Assert.Equal((1, 1), ParallelFanOut.CountLostBundles(stdout, stderr));
    }

    [Fact]
    public void WasResumed_ReadsTheNoticeAtTheStartOfAStderrLine_AndNothingQuotedMidLine()
    {
        Assert.True(AbortResume.WasResumed("x\n" + AbortResume.AttemptEndedNotice + ". Continuing\n"));
        Assert.False(AbortResume.WasResumed("a test said: " + AbortResume.AttemptEndedNotice + "\n"));
        Assert.False(AbortResume.WasResumed(""));
    }

    /// <summary>Only a whole summary line is an attempt boundary: a line at column 0 with its first four fields
    /// but no `Time: … (wall …)` tail, and the full line quoted mid-line, are message text. Two same-label bundles around either
    /// are two bundles, in one attempt.</summary>
    [Fact]
    public void Attempts_AreCutOnlyAtAWholeSummaryLineAtTheStartOfALine()
    {
        var prefixOnly = Suite("t") + "\nTests: 3   passed 1   failed 0   errors 1\n" + Suite("t") + "\n" + SummaryLine + "\n";
        var midLine = Suite("t") + "\nmessage " + SummaryLine + "\n" + Suite("t") + "\n" + SummaryLine + "\n";

        Assert.Equal(2, ParallelFanOut.CountBundleHeaders(prefixOnly, Errors, resumed: true));
        Assert.Equal(2, ParallelFanOut.CountBundleHeaders(midLine, Errors, resumed: true));
    }

    /// <summary>A shared bundle both of whose workers resumed: each printed the header in both of its
    /// attempts, which is one bundle seen by two workers, so one extra sighting, not three.</summary>
    [Fact]
    public void ExtraSightings_AResumedWorkersAttemptsAreNotExtraSightings()
    {
        var header = "=== a — SUITE ERRORS (";
        var resumed = Attempt(Suite("a", 2)) + Attempt(Suite("a"));
        var alsoResumed = Attempt(Suite("a")) + Attempt(Suite("a"));

        Assert.Equal(1, ParallelFanOut.ExtraSightings(new[] { resumed, alsoResumed }, new[] { 0, 1 }, header, new[] { true, true }));
        Assert.Equal(1, ParallelFanOut.CountAcrossAttempts(resumed, header, resumed: true));
    }

    // ── #5333: a worker that stopped before it reported prints no header, so the bundles it held are counted ──

    private static IReadOnlyList<IReadOnlyList<string>> Shards(params string[][] shards) => shards;
    private static string[] Out(params string[] perShard) => perShard;
    private static readonly bool[] NoneResumed = { false, false, false };

    [Fact]
    public void CountWorkersThatLeftNothing_ABundleOnALostWorker_IsOneNotRun()
    {
        var shards = Shards(new[] { "/b/ghost" }, new[] { "/b/clean" });

        Assert.Equal((1, 0), ParallelFanOut.CountWorkersThatLeftNothing(shards, new[] { false, true }, Out("", "=== clean ===\n"), NoneResumed));
    }

    /// <summary>The whole worker stopped, so every bundle it was handed is missing, not only the one that caused it.</summary>
    [Fact]
    public void CountWorkersThatLeftNothing_EveryBundleTheLostWorkerHeld_IsNotRun()
    {
        var shards = Shards(new[] { "/b/one", "/b/two" }, new[] { "/b/three" });

        Assert.Equal((2, 0), ParallelFanOut.CountWorkersThatLeftNothing(shards, new[] { false, true }, Out("", ""), NoneResumed));
    }

    [Fact]
    public void CountWorkersThatLeftNothing_WhenEveryWorkerReported_AddsNothing()
    {
        var shards = Shards(new[] { "/b/one" }, new[] { "/b/two" });

        Assert.Equal((0, 0), ParallelFanOut.CountWorkersThatLeftNothing(shards, new[] { true, true }, Out("", ""), NoneResumed));
    }

    /// <summary>A bundle two workers share, both lost: one bundle, not two.</summary>
    [Fact]
    public void CountWorkersThatLeftNothing_ASharedBundleEveryHolderLost_IsOneNotRun()
    {
        var shards = Shards(new[] { "/b/shared" }, new[] { "/b/shared" });

        Assert.Equal((1, 0), ParallelFanOut.CountWorkersThatLeftNothing(shards, new[] { false, false }, Out("", ""), NoneResumed));
    }

    /// <summary>One holder lost and the other ran its share: the bundle's other tests are in the totals, so it is partial, once.</summary>
    [Fact]
    public void CountWorkersThatLeftNothing_ASharedBundleOneHolderLost_IsOnePartial()
    {
        var shards = Shards(new[] { "/b/shared" }, new[] { "/b/shared" }, new[] { "/b/shared" });

        Assert.Equal((0, 1), ParallelFanOut.CountWorkersThatLeftNothing(shards, new[] { false, false, true }, Out("", "", "=== shared ===\n"), NoneResumed));
    }

    /// <summary>The holder that reported already printed the bundle's COMPILE FAIL header, which counted it: not counted twice.</summary>
    [Fact]
    public void CountWorkersThatLeftNothing_ASharedBundleAlreadyReportedNotRunByAHolder_AddsNothing()
    {
        var shards = Shards(new[] { "/b/shared" }, new[] { "/b/shared" });

        Assert.Equal((0, 0), ParallelFanOut.CountWorkersThatLeftNothing(shards, new[] { false, true },
            Out("", "=== shared — COMPILE FAIL ===\n"), NoneResumed));
    }

    /// <summary>The same for a SUITE ERRORS header: the holder's own count of the bundle as partial stands, once.</summary>
    [Fact]
    public void CountWorkersThatLeftNothing_ASharedBundleAlreadyReportedPartialByAHolder_AddsNothing()
    {
        var shards = Shards(new[] { "/b/shared" }, new[] { "/b/shared" });

        Assert.Equal((0, 0), ParallelFanOut.CountWorkersThatLeftNothing(shards, new[] { false, true },
            Out("", "=== shared — SUITE ERRORS (2) ===\n"), NoneResumed));
    }
}
