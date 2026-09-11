// IncompleteCoverageOutcomeTests — #3884: the exit-code policy for a coverage report built
// from a source map that could not read everything.
//
// The policy exists because three callers have to agree on it — the CLI `--coverage` path,
// the server's `runTests`, and the server's `execute` — and two of the three shipped
// disagreeing, because the decision was written inline at each site where nothing could test
// it and nothing made them match.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public class IncompleteCoverageOutcomeTests
{
    /// <summary>
    /// The rule this whole change is an instance of: could-not-measure must not leave on the
    /// exit-0 path (`.claude/rules/guards-need-a-third-state.md`). A coverage number computed
    /// over an unknown subset of the sources is not a coverage number.
    /// </summary>
    [Fact]
    public void AnIncompleteMapWithAReport_EscalatesACleanRunToTwo()
        => Assert.Equal(2, IncompleteCoverageOutcome.Apply(0, mapIsIncomplete: true, coverageWasProduced: true));

    /// <summary>
    /// The constraint, in the direction that matters most: a complete scan must stay a pass.
    /// A policy that escalated unconditionally would turn every green run red.
    /// </summary>
    [Fact]
    public void ACompleteMap_LeavesACleanRunAlone()
        => Assert.Equal(0, IncompleteCoverageOutcome.Apply(0, mapIsIncomplete: false, coverageWasProduced: true));

    /// <summary>
    /// A run that is ALREADY failing keeps its own code. The caller's first problem is the one
    /// they have — a failing test, a compile error — and overwriting it with 2 would lose it.
    /// </summary>
    [Theory]
    [InlineData(1)]  // a test failed
    [InlineData(3)]  // a bundle did not compile
    [InlineData(4)]  // a count-baseline mismatch
    public void ARunThatIsAlreadyFailing_KeepsItsOwnCode(int already)
        => Assert.Equal(already,
            IncompleteCoverageOutcome.Apply(already, mapIsIncomplete: true, coverageWasProduced: true));

    /// <summary>
    /// No coverage artifact, no claim about one. When the write failed, or the caller never
    /// asked for coverage, the run has a different and louder problem and this is not the
    /// thing to report — the CLI's unwritable-output path already owns that case
    /// (#3884 Copilot review).
    /// </summary>
    [Fact]
    public void AnIncompleteMapWithNoReport_ChangesNothing()
        => Assert.Equal(0, IncompleteCoverageOutcome.Apply(0, mapIsIncomplete: true, coverageWasProduced: false));
}
