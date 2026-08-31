// DescribeCrossMajorNoteTests — proves BcArtifacts.DescribeCrossMajorNote, the pure core
// behind #2210's fix, the same shape as EngineMinorMismatchWarningTests proves
// DescribeExplicitEngineMinorMismatch: a pure function over explicit values is provable
// with no BC engine or CLI invocation involved.
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
        Assert.DoesNotContain("[bc] warning:", message);
        // Reads as informational, not an alarm.
        Assert.StartsWith("[bc] note:", message);
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
