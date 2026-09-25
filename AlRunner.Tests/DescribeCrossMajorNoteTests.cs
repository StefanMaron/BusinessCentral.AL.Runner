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
using System;
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

    /// <summary>
    /// #2230: BOTH halves of Program.cs's variants if/else must say the same thing about the
    /// same fact. The variants-shipped branch grew its own hand-rolled wording -- "warning:
    /// project app.json targets BC major X but the latest cached artifact is Y" -- while the
    /// no-variants branch was reworded by #2210 to this function's measured, non-alarming
    /// note. Same declared-floor-vs-selected-major comparison, two vocabularies.
    ///
    /// <para>Nothing pinned the sibling's text, which is why it drifted and stayed drifted:
    /// grepping AlRunner.Tests for "targets BC major" found no test at all. This asserts the
    /// property that matters -- one wording, produced by one function -- rather than the
    /// literal string, so a future rewording moves both call sites or fails here.</para>
    ///
    /// <para>Deliberately a PURE test. Proving it end-to-end needs a variants-shipped
    /// install (EngineVariants.Discover finding a populated variants/ dir), which is what
    /// #2230 records as the expensive half; the wording question does not need it.</para>
    /// </summary>
    [Fact]
    public void BothProgramBranches_DescribeTheSameFactWithOneWording()
    {
        var note = BcArtifacts.DescribeCrossMajorNote("27", 28);
        Assert.NotNull(note);

        // The retired vocabulary, from the hand-rolled sibling. "warning" framed a measured
        // non-event as a hazard; "targets"/"latest cached artifact" is a second name for what
        // this function calls "declares"/"resolves against".
        Assert.DoesNotContain("targets BC major", note);
        Assert.DoesNotContain("latest cached artifact", note);
        Assert.DoesNotContain("warning", note, StringComparison.OrdinalIgnoreCase);

        // ...and the wording that replaces it, which both branches now share.
        Assert.Contains("declares BC major", note);
        Assert.Contains("a minimum, not a pin", note);
    }

    /// <summary>
    /// #2230, the half the wording test above cannot reach: that the variants-shipped branch
    /// actually CALLS this function rather than carrying a second copy of the sentence.
    ///
    /// <para>Asserted over Program.cs's source because the branch it guards needs a
    /// variants-shipped install to execute (EngineVariants.Discover finding a populated
    /// variants/ dir), and a test that cannot run the branch cannot observe its output. The
    /// property is still checkable: the retired literal must not reappear anywhere in the
    /// file, and both halves must reach the note through the shared function.</para>
    ///
    /// <para>Trap for whoever edits this: a source scan proves the call EXISTS, never that it
    /// fires. It is deliberately paired with the wording test above, which proves what the
    /// function says. Neither alone is the claim.</para>
    /// </summary>
    [Fact]
    public void ProgramCs_CarriesNoSecondCopyOfTheCrossMajorSentence()
    {
        var program = ReadProgramCs();

        // The hand-rolled sibling's literals. Present in a comment explaining the history is
        // fine; present in a Console.Error.WriteLine is the defect returning.
        foreach (var line in program.Split('\n'))
        {
            if (line.TrimStart().StartsWith("//")) continue;
            Assert.DoesNotContain("targets BC major", line);
            Assert.DoesNotContain("latest cached artifact is", line);
        }

        // Both halves of the variants if/else reach the note through the one function.
        var calls = System.Text.RegularExpressions.Regex.Matches(
            program, @"DescribeCrossMajorNote\s*\(").Count;
        Assert.True(calls >= 2,
            $"expected both Program.cs branches to call DescribeCrossMajorNote, found {calls}");
    }

    private static string ReadProgramCs()
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "AlRunner", "Program.cs")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, "AlRunner", "Program.cs"));
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
