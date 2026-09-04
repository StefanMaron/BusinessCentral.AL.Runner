// AbortResumePlanTests — turning a watchdog abort into the next attempt (issue #2280).
//
// One hung codeunit must not take down the whole run. It currently does: TestExecutor abandons
// the rest of that codeunit AND every later codeunit in the bundle, because the hung thread is
// never killed and keeps mutating shared BC state — so continuing IN-PROCESS would report
// results that lie. The state is only trustworthy again in a fresh process.
//
// So the runner re-runs the remainder in a new process with the hung codeunit excluded, and
// repeats while it keeps making progress. This is the part that reads an abort and decides what
// the next attempt must skip; getting it wrong either loops forever or excludes too much.
//
// Measured on Tests-ERM (BC 28.1, --test-data): 2 tests before, 1,066 after excluding the first
// hung codeunit, 2,145 after the second.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class AbortResumePlanTests
{
    private const string Abort1 =
        "ERM Close Income Statement (Codeunit134228).CloseIncomeStatementTwice: watchdog timeout "
        + "aborted the run — 15 further [Test] method(s) in this codeunit and 9483 in 130 subsequent "
        + "codeunit(s) did not run (9498 total)";

    /// <summary>The codeunit is what gets excluded, not the single method: the same codeunit
    /// almost always hangs again on the next method, and excluding one method at a time costs a
    /// whole process per method.</summary>
    [Fact]
    public void NextExclusions_TakesTheHungCodeunit()
    {
        var next = AbortResumePlan.NextExclusions(new[] { Abort1 }, already: Array.Empty<string>());

        Assert.Equal(new[] { "Codeunit134228" }, next);
    }

    /// <summary>Exclusions accumulate across attempts — attempt 3 must still skip what attempts
    /// 1 and 2 found, or it walks straight back into the first hang.</summary>
    [Fact]
    public void NextExclusions_AccumulateWithWhatIsAlreadyExcluded()
    {
        var next = AbortResumePlan.NextExclusions(
            new[] { "X (Codeunit134043).Foo: watchdog timeout aborted the run — 7 further" },
            already: new[] { "Codeunit134228" });

        Assert.Contains("Codeunit134228", next);
        Assert.Contains("Codeunit134043", next);
        Assert.Equal(2, next.Count);
    }

    /// <summary>Several aborts in one run (one per bundle) all contribute.</summary>
    [Fact]
    public void NextExclusions_HandlesSeveralAbortsFromOneRun()
    {
        var next = AbortResumePlan.NextExclusions(
            new[] { Abort1, "Y (Codeunit137309).Bar: watchdog timeout aborted the run — 28 further" },
            already: Array.Empty<string>());

        Assert.Equal(new[] { "Codeunit134228", "Codeunit137309" }, next.OrderBy(x => x).ToArray());
    }

    /// <summary>The loop must terminate. If an abort names a codeunit that is ALREADY excluded,
    /// no new exclusion is available and retrying would repeat forever — the caller has to see
    /// "no progress" rather than a silently identical next attempt.</summary>
    [Fact]
    public void NextExclusions_ReturnsNoNewEntries_WhenTheHungCodeunitIsAlreadyExcluded()
    {
        var already = new[] { "Codeunit134228" };

        var next = AbortResumePlan.NextExclusions(new[] { Abort1 }, already);

        Assert.Equal(already, next);
        Assert.False(AbortResumePlan.MakesProgress(new[] { Abort1 }, already));
    }

    /// <summary>Positive counterpart: a genuinely new hang IS progress, so the loop continues.</summary>
    [Fact]
    public void MakesProgress_WhenTheHangIsSomewhereNew()
        => Assert.True(AbortResumePlan.MakesProgress(new[] { Abort1 }, new[] { "Codeunit999" }));

    /// <summary>Negative: a line that is not an abort reason contributes nothing rather than
    /// producing a garbage exclusion that would silently drop real tests.</summary>
    [Fact]
    public void NextExclusions_IgnoresUnparseableLines()
    {
        var next = AbortResumePlan.NextExclusions(
            new[] { "something else entirely", "" }, already: Array.Empty<string>());

        Assert.Empty(next);
    }

    /// <summary>No aborts means nothing to resume — the caller must not spawn another process.</summary>
    [Fact]
    public void MakesProgress_IsFalse_WithNoAborts()
        => Assert.False(AbortResumePlan.MakesProgress(Array.Empty<string>(), Array.Empty<string>()));

    /// <summary>Resume exists to recover the LATER codeunits a hang took down. An abort that
    /// abandoned nothing beyond the hung codeunit is not worth a retry: it would re-run only what
    /// already ran, minus the hung one, for the price of a full BC boot — and would turn a loud
    /// abort into a bundle reporting "0 tests, 0 suite errors", the original bug's own signature.
    /// SuiteAbortOnTimeoutTests' fixture is exactly this shape and caught it.</summary>
    [Fact]
    public void MakesProgress_IsFalse_WhenTheHangTookNoLaterCodeunitsDown()
    {
        var onlyThisCodeunit =
            "Suite Abort On Timeout Tests (Codeunit50100).Hangs: watchdog timeout aborted the run — "
            + "2 further [Test] method(s) in this codeunit did not run (2 total)";

        Assert.False(AbortResumePlan.AbandonedLaterCodeunits(onlyThisCodeunit));
        Assert.False(AbortResumePlan.MakesProgress(new[] { onlyThisCodeunit }, Array.Empty<string>()));
    }

    /// <summary>Positive counterpart, so the guard above cannot be satisfied by refusing every
    /// resume: an abort that DID take later codeunits down is worth retrying.</summary>
    [Fact]
    public void MakesProgress_IsTrue_WhenLaterCodeunitsWereAbandoned()
    {
        Assert.True(AbortResumePlan.AbandonedLaterCodeunits(Abort1));
        Assert.True(AbortResumePlan.MakesProgress(new[] { Abort1 }, Array.Empty<string>()));
    }
}
