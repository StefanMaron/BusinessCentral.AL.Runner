// DescribeCrossMajorNoteTests — proves BcArtifacts.DescribeCrossMajorNote, the pure core
// behind #2210's fix, the same shape as EngineMinorMismatchWarningTests proves
// DescribeExplicitEngineMinorMismatch: a pure function over explicit values is provable
// with no BC engine or CLI invocation involved.
//
// The function returns a bare message BODY, no tag — both call sites (the main auto-select
// path in Program.cs, tagged "[bc] note: ", and al-runner provision's
// ResolveDefaultProvisionVersion, tagged "[provision] note: ") prepend their own tag so the
// two never double-tag it. See CrossMajorNoteTests.cs for the end-to-end proof that the
// note is gated on --verbose and never appears at default verbosity.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public class DescribeCrossMajorNoteTests
{
    [Fact]
    public void MismatchedMajor_ReturnsAccurateNote_NotOldAlarmingWording()
    {
        var message = BcArtifacts.DescribeCrossMajorNote("27", 28);

        Assert.NotNull(message);
        Assert.Contains("27", message);
        Assert.Contains("28", message);
        // The old, retired wording claimed a hazard the #2210 measurement did not find.
        Assert.DoesNotContain("needs a matching runner build", message);
        // No baked-in tag — see the class doc comment. Callers own the tag.
        Assert.DoesNotContain("[bc]", message);
        Assert.DoesNotContain("[provision]", message);
    }

    [Fact]
    public void SameMajor_ReturnsNull()
    {
        Assert.Null(BcArtifacts.DescribeCrossMajorNote("28", 28));
    }

    [Fact]
    public void NullProjectMajor_ReturnsNull()
    {
        // No derivable app.json major (e.g. no app.json found, or an unparseable one) —
        // nothing to compare against, so no note.
        Assert.Null(BcArtifacts.DescribeCrossMajorNote(null, 28));
    }

    [Fact]
    public void DifferentMajor_TrailingByMoreThanOne_StillNoted()
    {
        // Not just the adjacent-major case #2210 measured directly — any mismatch is worth
        // surfacing, even though the note's own text only speaks to what was measured.
        var message = BcArtifacts.DescribeCrossMajorNote("25", 28);

        Assert.NotNull(message);
        Assert.Contains("25", message);
        Assert.Contains("28", message);
    }
}
