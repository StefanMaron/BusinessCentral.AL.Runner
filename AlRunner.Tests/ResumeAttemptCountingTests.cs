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

    private static string Attempt(params string[] lines) =>
        string.Join("\n", lines) + "\nTests: 3   passed 1   failed 0   errors 1        Time: 1.0 s (wall 2.0 s)\n";

    [Fact]
    public void CountBundleHeaders_OneAttempt_CountsEachBundle()
    {
        var output = Attempt("=== a — SUITE ERRORS (1) ===", "=== b — SUITE ERRORS (2) ===");

        Assert.Equal(2, ParallelFanOut.CountBundleHeaders(output, Errors));
    }

    /// <summary>The shape of #5269: the worker resumed, and the bundle lost a suite in both attempts.</summary>
    [Fact]
    public void CountBundleHeaders_ABundleReportedByEveryAttempt_IsOneBundle()
    {
        var output = Attempt("=== a — SUITE ERRORS (2) ===") + "resume: x\n" + Attempt("=== a — SUITE ERRORS (1) ===");

        Assert.Equal(1, ParallelFanOut.CountBundleHeaders(output, Errors));
    }

    /// <summary>The sibling shape, which #5269 said it had not run: COMPILE FAIL is counted the same way.</summary>
    [Fact]
    public void CountBundleHeaders_ACompileFailReportedByEveryAttempt_IsOneBundle()
    {
        var output = Attempt("=== a — COMPILE FAIL ===") + Attempt("=== a — COMPILE FAIL ===");

        Assert.Equal(1, ParallelFanOut.CountBundleHeaders(output, CompileFail));
    }

    /// <summary>A bundle that reported in one attempt only (a watchdog abort is not repeated) still counts,
    /// and a different bundle in the next attempt is a second one: the attempts are not collapsed.</summary>
    [Fact]
    public void CountBundleHeaders_DifferentBundlesInDifferentAttempts_AreDifferentBundles()
    {
        var output = Attempt("=== a — SUITE ERRORS (1) ===") + Attempt("=== b — SUITE ERRORS (1) ===");

        Assert.Equal(2, ParallelFanOut.CountBundleHeaders(output, Errors));
    }

    /// <summary>Two bundles that share a label in one attempt are two bundles; only a repeat across
    /// attempts is taken back.</summary>
    [Fact]
    public void CountBundleHeaders_TwoBundlesSharingALabelInOneAttempt_AreTwo()
    {
        var output = Attempt("=== tests — SUITE ERRORS (1) ===", "=== tests — SUITE ERRORS (1) ===")
            + Attempt("=== tests — SUITE ERRORS (1) ===", "=== tests — SUITE ERRORS (1) ===");

        Assert.Equal(2, ParallelFanOut.CountBundleHeaders(output, Errors));
    }

    /// <summary>A worker that died before it printed a counts line has no attempts to separate: what it
    /// printed is one attempt, counted as before.</summary>
    [Fact]
    public void CountBundleHeaders_WithNoCountsLine_CountsTheWholeOutputAsOneAttempt()
    {
        Assert.Equal(2, ParallelFanOut.CountBundleHeaders(
            "=== a — SUITE ERRORS (1) ===\n=== b — SUITE ERRORS (1) ===\n", Errors));
        Assert.Equal(0, ParallelFanOut.CountBundleHeaders("", Errors));
    }

    /// <summary>A shared bundle both of whose workers resumed: each printed the header in both of its
    /// attempts, which is one bundle seen by two workers, so one extra sighting, not three.</summary>
    [Fact]
    public void ExtraSightings_AResumedWorkersAttemptsAreNotExtraSightings()
    {
        var header = "=== a — SUITE ERRORS (";
        var resumed = Attempt(header + "2) ===") + Attempt(header + "1) ===");
        var alsoResumed = Attempt(header + "1) ===") + Attempt(header + "1) ===");

        Assert.Equal(1, ParallelFanOut.ExtraSightings(new[] { resumed, alsoResumed }, new[] { 0, 1 }, header));
        Assert.Equal(1, ParallelFanOut.CountAcrossAttempts(resumed, header));
    }
}
