// IncompleteCoverageOutcome — what an incomplete source map does to a run's exit code.
//
// One line of policy, in one place, because three callers have to agree on it: the CLI
// --coverage path, the server's runTests, and the server's execute. Two of the three shipped
// disagreeing (#3884 Copilot review) — the CLI escalated and both server handlers returned an
// ordinary success carrying a short table — and the way that happened is that the decision was
// written inline at each site, so nothing could be tested and nothing made them match.
namespace AlRunner.Infrastructure;

public static class IncompleteCoverageOutcome
{
    /// <summary>
    /// The exit code a run should report, given what it would otherwise have reported.
    ///
    /// <para>An incomplete map means coverage was computed over an unknown subset of the
    /// sources, so the number is not a coverage number and the run must not leave on the
    /// exit-0 path (`.claude/rules/guards-need-a-third-state.md`). A run that is ALREADY
    /// failing keeps its own code: the caller's first problem is the failure they have, not
    /// this one, and overwriting it would lose it.</para>
    /// </summary>
    /// <param name="currentExitCode">What the run would report without this consideration.</param>
    /// <param name="mapIsIncomplete">Whether the source map reported any scan failure.</param>
    /// <param name="coverageWasProduced">Whether a coverage artifact or table actually exists.
    /// When it does not, the run has a different and louder problem and this one is not the
    /// thing to report.</param>
    public static int Apply(int currentExitCode, bool mapIsIncomplete, bool coverageWasProduced)
        => mapIsIncomplete && coverageWasProduced && currentExitCode == 0 ? 2 : currentExitCode;
}
