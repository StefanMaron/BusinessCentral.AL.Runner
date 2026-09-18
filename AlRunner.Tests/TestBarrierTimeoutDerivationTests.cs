// TestBarrierTimeoutDerivationTests — #4309.
//
// TestBarrier.WaitForRelease bounds its wait at 60s and reports that figure in the
// TimeoutException it throws five lines later. Those were two literals: the message repeated the
// cap rather than deriving it, so it was invisible while the two agreed and wrong the moment
// someone moved one and not the other.
//
// That is #3488/#4275's defect class, and #3435 is the measurement that settles why it matters: a
// cap squeezed to 3s still reported "did not exit within 120s", which sends a reader looking for a
// two-minute hang that never happened.
//
// Scanned as SOURCE rather than executed, because the observable is a 60-second wait — a test that
// drove it would cost a minute to prove a one-line property. SpawnTimeoutMessageDerivationTests
// makes the same trade for the same reason; this file exists separately because that guard's roster
// is AlRunner.Tests/ and TestBarrier is production code under AlRunner/Infrastructure/ (#4309).
using Xunit;

namespace AlRunner.Tests;

public sealed class TestBarrierTimeoutDerivationTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <summary>
    /// Source with COMMENTS blanked and literals KEPT.
    ///
    /// <para>Not <c>CodeOnly</c>, which was the first attempt and cannot see this defect at all:
    /// it blanks literal content, so a regressed <c>"within 60s"</c> disappears from the scanned
    /// text and the only 60 left is the constant's own declaration. Both of that version's
    /// assertions passed against a message that had been regressed to a literal — the check was
    /// blind to exactly what it was written for.</para>
    ///
    /// <para>Comments still go, because the doc comment above <c>WaitForRelease</c> legitimately
    /// says "Bounded at 60s" in prose and must neither satisfy nor break this.</para>
    /// </summary>
    private static string Source() => CSharpSource.CommentsBlanked(
        File.ReadAllText(Path.Combine(RepoRoot, "AlRunner", "Infrastructure", "TestBarrier.cs")));

    /// <summary>
    /// The deadline and the message must both come from one constant. Asserting on CodeOnly
    /// source, so the doc comment above WaitForRelease — which legitimately says "Bounded at 60s"
    /// in prose — cannot satisfy or break this.
    /// </summary>
    [Fact]
    public void WaitForRelease_DerivesItsReportedFigureFromTheDeadlineItApplied()
    {
        var src = Source();

        // One spelling of the cap in the whole file, literals included. A second means the
        // message (or a second deadline) carries its own copy, which is the defect: one of the
        // two will eventually move alone.
        var literals = System.Text.RegularExpressions.Regex.Matches(src, @"(?<![\w.])60(?![\w.])")
            .Count;
        Assert.True(literals <= 1,
            $"TestBarrier.cs spells 60 {literals} times. The cap belongs in ONE constant that both "
            + "the deadline and the message read, or the two drift apart silently (#4309, and "
            + "#3435 for what that costs).");

        // ...and the message must READ that constant rather than carry its own copy.
        //
        // Asserted through the interpolation HOLE, not the message text: CodeOnly blanks literal
        // content but deliberately leaves the expressions inside {…} standing, because a hole can
        // call something (CSharpSource, #3527). So a derived message leaves the constant's name
        // visible here and a hardcoded one leaves nothing — which is exactly the distinction this
        // test is about, and it is why scanning the raw text would be weaker: there, "within 60s"
        // inside a string and 60 in the deadline look the same.
        // Asserted on the MESSAGE, not on the file: the constant stays declared under a
        // regression, so "the name appears somewhere" passes while the sentence carries a
        // literal — measured, that mutation was GREEN before this line read the message itself.
        var message = System.Text.RegularExpressions.Regex.Match(src, @"within [^""]*s ");
        Assert.True(message.Success, "no `within …s` phrase found in TestBarrier.cs at all — the "
            + "message was reworded, so this test is measuring nothing. Re-point it.");
        Assert.Contains("ReleaseWaitSeconds", message.Value, StringComparison.Ordinal);
    }
}
