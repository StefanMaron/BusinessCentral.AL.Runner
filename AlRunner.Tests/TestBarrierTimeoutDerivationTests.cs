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
    /// Both the deadline and the message must READ the constant, asserted structurally rather
    /// than by counting a number.
    ///
    /// <para>Counting was the first version and it guarded the deadline only by accident of the
    /// cap's current value: raise the cap to 90 legitimately, hardcode the deadline to 90, and a
    /// `60`-keyed count sees nothing — the identical defect, passing. Worse, the regex it used
    /// could not match `60s` at all, because `s` is a word character, so the check the comment
    /// described was never performed (#4309, measured in review).</para>
    ///
    /// <para>A number-keyed assertion also has to be edited by anyone legitimately changing the
    /// cap, which is the coupling this issue exists to remove — `SpawnTimeoutMessageDerivation`'s
    /// header says the same thing about pinning `120s`.</para>
    /// </summary>
    [Fact]
    public void WaitForRelease_DerivesItsReportedFigureFromTheDeadlineItApplied()
    {
        var src = Source();

        // The deadline half: whatever AddSeconds is given, it must be the constant.
        var deadline = System.Text.RegularExpressions.Regex.Match(src, @"AddSeconds\(([^)]*)\)");
        Assert.True(deadline.Success,
            "no AddSeconds(...) call found in TestBarrier.cs, so this test is measuring nothing "
            + "about the deadline. The wait was restructured — re-point this.");
        Assert.Equal("ReleaseWaitSeconds", deadline.Groups[1].Value.Trim());

        // The message half: EVERY `within …s` phrase must interpolate the same constant.
        //
        // All matches, not the first. A first-match-wins read is satisfied by any one derived
        // phrase, so an earlier string naming the constant let a fully regressed message pass —
        // measured in review, GREEN with `within 60s` in the actual throw. Requiring every
        // occurrence to be derived removes the ordering dependency entirely: a second phrase can
        // only help if it is correct too.
        var phrases = System.Text.RegularExpressions.Regex.Matches(src, @"within ([^""]{0,40}?)s[ ""]");
        Assert.True(phrases.Count > 0,
            "no `within …s` phrase found in TestBarrier.cs at all — the message was reworded, so "
            + "this test measures nothing, which is not the same as passing. Re-point it.");
        foreach (System.Text.RegularExpressions.Match phrase in phrases)
        {
            Assert.Equal("{ReleaseWaitSeconds}", phrase.Groups[1].Value.Trim());
        }
    }
}
