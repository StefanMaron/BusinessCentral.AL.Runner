// WatchOutputSlicingTests — deterministic RED/GREEN proof for #1843, over a synthetic line
// sequence instead of a live-process race. See WatchOutputSlicing.cs's header for the full
// mechanism (stdout/stderr pumps racing into one merged list).
using Xunit;

namespace AlRunner.Tests;

public sealed class WatchOutputSlicingTests
{
    private const string TimingNeedle = "GetSharedReferences";

    /// <summary>
    /// Builds the exact list shape a starved stderr pump produces: cycle 1's stdout chatter,
    /// the m1 marker, cycle 2's stdout chatter (FAIL + fixture name), the m2 marker — and
    /// ONLY THEN, appended after m2, the stderr timing line that was actually written, in
    /// program order, during cycle 2 (before m2). That inversion is the bug: the write
    /// happened before m2, but the pump's ReadLineAsync continuation that appends it to the
    /// shared list got scheduled after the stdout pump's append of m2.
    /// </summary>
    private static (List<CapturedLine> lines, int m1, int m2) StarvedStderrPumpScenario()
    {
        var lines = new List<CapturedLine>
        {
            new(OutputStream.Stdout, "PASS  Codeunit 60001 Insert_OnInsertReadsXRec_BuildsConcreteBeforeImage"),
        };
        int m1 = lines.Count;
        lines.Add(new(OutputStream.Stdout, WatchOutputSlicing.WaitingForSourceMarker + "… (Ctrl+C to quit)"));

        lines.Add(new(OutputStream.Stdout, "[watch] change detected — re-running…"));
        lines.Add(new(OutputStream.Stdout, "FAIL  Codeunit 60001 Insert_OnInsertReadsXRec_BuildsConcreteBeforeImage"));
        int m2 = lines.Count;
        lines.Add(new(OutputStream.Stdout, WatchOutputSlicing.WaitingForSourceMarker + "… (Ctrl+C to quit)"));

        // Written during cycle 2, strictly before m2 in program order — but its pump
        // continuation lost the race and only got appended to the shared list here.
        lines.Add(new(OutputStream.Stderr, "[emit-timing] GetSharedReferences (5 specs): 12ms"));

        return (lines, m1, m2);
    }

    /// <summary>
    /// THE PROOF THAT MATTERS. Cycle 2's timing search must find the diagnostic even though
    /// its list index is past m2 — because it was written, in program order, before m2. This
    /// is the RED/GREEN pivot: before the fix, CycleTimingSearchText bounded its scan at
    /// `to` (mirroring the old Segment(m1+1, m2) window) and this assertion failed. After the
    /// fix, the stderr search ignores `to` entirely and finds it.
    /// </summary>
    [Fact]
    public void CycleTimingSearchText_FindsTimingLine_EvenWhenStderrPumpIsStarvedPastTheNextStdoutMarker()
    {
        var (lines, m1, m2) = StarvedStderrPumpScenario();

        var searchText = WatchOutputSlicing.CycleTimingSearchText(lines, m1 + 1, m2);

        Assert.Contains(TimingNeedle, searchText);
        var match = System.Text.RegularExpressions.Regex.Match(
            searchText, @"GetSharedReferences[^:]*:\s*(\d+)ms");
        Assert.True(match.Success, $"expected a parseable timing line, got:\n{searchText}");
        Assert.Equal(12, int.Parse(match.Groups[1].Value));
    }

    /// <summary>
    /// Negative/mutation companion: if the warm re-emit genuinely never wrote a timing line
    /// for cycle 2 (the feature is broken, or BCCOMPILER_TIMING wasn't honoured), the search
    /// text must NOT contain the needle. This is what stops the fix from degenerating into
    /// "always return everything, so the assertion always finds something" — a fix that just
    /// concatenated the WHOLE list unconditionally would pass the positive test above but
    /// also pass this one falsely if it leaked content across an unrelated boundary. Here we
    /// only remove the diagnostic line entirely: with it gone, even an unbounded-forward
    /// search must correctly report nothing.
    /// </summary>
    [Fact]
    public void CycleTimingSearchText_FindsNothing_WhenNoWarmTimingLineWasEverWritten()
    {
        var (lines, m1, m2) = StarvedStderrPumpScenario();
        lines.RemoveAt(lines.Count - 1); // drop the stderr timing line entirely

        var searchText = WatchOutputSlicing.CycleTimingSearchText(lines, m1 + 1, m2);

        Assert.DoesNotContain(TimingNeedle, searchText);
    }

    /// <summary>
    /// Sanity check on the marker finder itself: it must key off the stream, not just text,
    /// so a stderr line that happens to contain the marker substring cannot be mistaken for
    /// the real watch-loop marker (which is stdout-only — Program.cs:1916).
    /// </summary>
    [Fact]
    public void FindStdoutMarkerIndices_IgnoresStderrLinesContainingTheMarkerText()
    {
        var lines = new List<CapturedLine>
        {
            new(OutputStream.Stderr, WatchOutputSlicing.WaitingForSourceMarker + " (not really — wrong stream)"),
            new(OutputStream.Stdout, WatchOutputSlicing.WaitingForSourceMarker + "… (Ctrl+C to quit)"),
        };

        var indices = WatchOutputSlicing.FindStdoutMarkerIndices(lines, WatchOutputSlicing.WaitingForSourceMarker);

        Assert.Equal(new[] { 1 }, indices);
    }

    /// <summary>
    /// MergedJoin still preserves stdout-vs-stdout relative order within the bounded window
    /// — the PASS/FAIL/fixture-name assertions are unaffected by this fix, since they only
    /// ever look at stdout content whose order is stable (single pump per stream).
    /// </summary>
    [Fact]
    public void MergedJoin_PreservesOrderAndBounds()
    {
        var (lines, m1, m2) = StarvedStderrPumpScenario();

        var cycle2 = WatchOutputSlicing.MergedJoin(lines, m1 + 1, m2);

        Assert.Contains("FAIL", cycle2);
        Assert.Contains("Insert_OnInsertReadsXRec_BuildsConcreteBeforeImage", cycle2);
        Assert.DoesNotContain(TimingNeedle, cycle2); // the starved stderr line is out of this window
    }
}
