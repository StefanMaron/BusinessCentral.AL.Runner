// ResumeSkipsCompletedTests — a resume must not re-run what already ran (issue #2280).
//
// The first version of the watchdog resume re-ran the bundle FROM THE START with the hung
// codeunit excluded. That is correct but wasteful, and the waste compounds badly:
//
//   * every codeunit that already ran runs again, so a bundle that hangs late pays for its
//     whole successful prefix a second time;
//   * under --jobs the unit of retry is the SHARD, so a shard holding eight buckets re-runs
//     all eight because one codeunit in one of them hung;
//   * the watchdog is WALL-CLOCK, so the extra load from re-running makes further spurious
//     aborts more likely, which triggers further re-runs.
//
// The fix needs no new selection mechanism: exclude every codeunit already ATTEMPTED, not just
// the hung one. What remains is exactly the untouched work.

using AlRunner;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class ResumeSkipsCompletedTests
{
    private static TestResult R(string codeunit, string method)
        => new(codeunit, method, TestOutcome.Pass, null, null, TimeSpan.Zero);

    private const string Abort =
        "X (Codeunit134228).Hangs: watchdog timeout aborted the run — 15 further [Test] method(s) "
        + "in this codeunit and 9483 in 130 subsequent codeunit(s) did not run (9498 total)";

    /// <summary>Codeunits that already produced results are excluded from the retry, so the
    /// retry runs only what was never reached.</summary>
    [Fact]
    public void NextExclusions_ExcludesEveryCodeunitAlreadyAttempted()
    {
        var attempted = new[] { R("Codeunit134001", "A"), R("Codeunit134002", "B"), R("Codeunit134228", "C") };

        var next = AbortResumePlan.NextExclusions(new[] { Abort }, Array.Empty<string>(), attempted);

        Assert.Contains("Codeunit134001", next);
        Assert.Contains("Codeunit134002", next);
        Assert.Contains("Codeunit134228", next);   // the hung one, from the abort reason
    }

    /// <summary>The hung codeunit is excluded even when it produced NO results — it can hang on
    /// its very first test, which is exactly the Tests-ERM case (2 tests ran of 9,500).</summary>
    [Fact]
    public void NextExclusions_ExcludesTheHungCodeunit_EvenWithNoResultsFromIt()
    {
        var next = AbortResumePlan.NextExclusions(new[] { Abort }, Array.Empty<string>(), Array.Empty<TestResult>());

        Assert.Equal(new[] { "Codeunit134228" }, next);
    }

    /// <summary>Earlier attempts' exclusions still accumulate, so attempt 3 does not re-run what
    /// attempt 1 and 2 already covered.</summary>
    [Fact]
    public void NextExclusions_AccumulateAcrossAttempts()
    {
        var next = AbortResumePlan.NextExclusions(
            new[] { Abort }, already: new[] { "Codeunit130000" }, attempted: new[] { R("Codeunit134001", "A") });

        Assert.Contains("Codeunit130000", next);
        Assert.Contains("Codeunit134001", next);
        Assert.Contains("Codeunit134228", next);
    }

    /// <summary>Duplicates collapse — a codeunit with twenty results contributes one exclusion,
    /// not twenty command-line arguments.</summary>
    [Fact]
    public void NextExclusions_AreDistinct()
    {
        var attempted = Enumerable.Range(0, 20).Select(i => R("Codeunit134001", $"M{i}")).ToArray();

        var next = AbortResumePlan.NextExclusions(new[] { Abort }, Array.Empty<string>(), attempted);

        Assert.Equal(1, next.Count(x => x == "Codeunit134001"));
    }

    /// <summary>Negative: with no abort there is nothing to resume, so attempted codeunits must
    /// NOT be turned into exclusions — that would silently skip them on some later run.</summary>
    [Fact]
    public void MakesProgress_IsFalse_WithNoAborts_EvenWithAttemptedCodeunits()
        => Assert.False(AbortResumePlan.MakesProgress(Array.Empty<string>(), Array.Empty<string>()));
}
