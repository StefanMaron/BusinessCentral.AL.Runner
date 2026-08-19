// WatchOutputSlicingTests — deterministic RED/GREEN proof for #1843, over a synthetic line
// sequence instead of a live-process race. See WatchOutputSlicing.cs's header for the full
// mechanism (stdout/stderr pumps racing into one merged list).
using System;
using System.Collections.Generic;
using Xunit;

namespace AlRunner.Tests;

public sealed class WatchOutputSlicingTests
{
    private const string TimingNeedle = "GetSharedReferences";

    /// <summary>
    /// Builds the exact list shape a starved stderr pump produces on the UPPER-bound side:
    /// cycle 1's stdout chatter, the m1 marker, cycle 2's stdout chatter (FAIL + fixture
    /// name), the m2 marker — and ONLY THEN, appended after m2, the stderr timing line that
    /// was actually written, in program order, during cycle 2 (before m2).
    /// </summary>
    private static (List<CapturedLine> lines, int m1, int m2) StarvedPastM2Scenario()
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
    /// Mirrors StarvedPastM2Scenario in the OTHER direction: cycle 1's COLD timing line
    /// (the ~40s reload) is also starved — its pump continuation loses the race against the
    /// m1 marker's stdout pump and lands AFTER m1, before cycle 2 even starts. Cycle 2's warm
    /// line follows normally, after m2. A fix that merely drops the upper bound and keeps
    /// scanning forward from m1+1 would read cycle 1's ~40000ms line as cycle 2's timing.
    /// </summary>
    private static (List<CapturedLine> lines, int m1, int m2) StarvedPastM1AndM2Scenario()
    {
        var lines = new List<CapturedLine>
        {
            new(OutputStream.Stdout, "PASS  Codeunit 60001 Insert_OnInsertReadsXRec_BuildsConcreteBeforeImage"),
        };
        int m1 = lines.Count;
        lines.Add(new(OutputStream.Stdout, WatchOutputSlicing.WaitingForSourceMarker + "… (Ctrl+C to quit)"));

        // Cycle 1's own (cold) timing line, starved past m1 — written before m1 in program
        // order, but scheduled onto the shared list after it.
        lines.Add(new(OutputStream.Stderr, "[emit-timing] GetSharedReferences (5 specs): 41000ms"));

        lines.Add(new(OutputStream.Stdout, "[watch] change detected — re-running…"));
        lines.Add(new(OutputStream.Stdout, "FAIL  Codeunit 60001 Insert_OnInsertReadsXRec_BuildsConcreteBeforeImage"));
        int m2 = lines.Count;
        lines.Add(new(OutputStream.Stdout, WatchOutputSlicing.WaitingForSourceMarker + "… (Ctrl+C to quit)"));

        // Cycle 2's warm line, also starved past m2 — the original #1843 shape.
        lines.Add(new(OutputStream.Stderr, "[emit-timing] GetSharedReferences (5 specs): 12ms"));

        return (lines, m1, m2);
    }

    /// <summary>
    /// THE ORIGINAL #1843 PROOF. Cycle 2's timing must be found even though its list index
    /// is past m2 — because it was written, in program order, before m2.
    /// </summary>
    [Fact]
    public void LastWarmTimingMs_FindsTiming_EvenWhenStderrPumpIsStarvedPastTheNextStdoutMarker()
    {
        var (lines, _, _) = StarvedPastM2Scenario();

        var elapsedMs = WatchOutputSlicing.LastWarmTimingMs(lines);

        Assert.Equal(12, elapsedMs);
    }

    /// <summary>
    /// THE MIRRORED PROOF (review round 2). A fix that just drops the upper bound and keeps
    /// scanning forward from m1+1 is exposed to the identical race in the other direction:
    /// cycle 1's cold ~41000ms line, starved past m1, would be the FIRST match in that
    /// unbounded-forward scan and get misread as cycle 2's warm timing. The real fix takes
    /// the LAST match across the entire (unbounded, in both directions) stderr stream —
    /// correct here because stderr has exactly one pump, so cycle 1's line is always before
    /// cycle 2's regardless of either line's position relative to the stdout markers.
    /// </summary>
    [Fact]
    public void LastWarmTimingMs_FindsCycle2sTiming_EvenWhenCycle1sColdLineIsAlsoStarvedPastM1()
    {
        var (lines, _, _) = StarvedPastM1AndM2Scenario();

        var elapsedMs = WatchOutputSlicing.LastWarmTimingMs(lines);

        Assert.Equal(12, elapsedMs);
    }

    /// <summary>
    /// Negative/mutation companion: if the warm re-emit genuinely never wrote a timing line
    /// for either cycle (the feature is broken, or BCCOMPILER_TIMING wasn't honoured), there
    /// is nothing to find. This is what stops the fix from degenerating into "always return
    /// something, so the assertion always finds something".
    /// </summary>
    [Fact]
    public void LastWarmTimingMs_ReturnsNull_WhenNoWarmTimingLineWasEverWritten()
    {
        var (lines, _, _) = StarvedPastM2Scenario();
        lines.RemoveAt(lines.Count - 1); // drop the stderr timing line entirely

        var elapsedMs = WatchOutputSlicing.LastWarmTimingMs(lines);

        Assert.Null(elapsedMs);
    }

    /// <summary>
    /// StderrText itself (used for the Assert.Contains presence check and diagnostic dump in
    /// WatchTests) must include every stderr line regardless of its position relative to
    /// stdout markers, and must exclude stdout content.
    /// </summary>
    [Fact]
    public void StderrText_IncludesAllStderrLines_RegardlessOfPositionRelativeToStdoutMarkers()
    {
        var (lines, _, _) = StarvedPastM1AndM2Scenario();

        var stderrText = WatchOutputSlicing.StderrText(lines);

        Assert.Contains("41000ms", stderrText);
        Assert.Contains("12ms", stderrText);
        Assert.DoesNotContain("FAIL", stderrText);
        Assert.DoesNotContain(WatchOutputSlicing.WaitingForSourceMarker, stderrText);
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
    /// Covers the `fromIndex` parameter WatchTests' WaitForMarkerAfter actually uses to poll
    /// for the NEXT marker after a given cycle start — this is the real call shape, not just
    /// the default-from-zero overload.
    /// </summary>
    [Fact]
    public void FindStdoutMarkerIndices_FromIndex_SkipsMarkersAtOrBeforeIt()
    {
        var (lines, m1, m2) = StarvedPastM2Scenario();

        var indices = WatchOutputSlicing.FindStdoutMarkerIndices(
            lines, WatchOutputSlicing.WaitingForSourceMarker, m1 + 1);

        Assert.Equal(new[] { m2 }, indices);
    }

    /// <summary>
    /// MergedJoin still preserves stdout-vs-stdout relative order within the bounded window
    /// — the PASS/FAIL/fixture-name assertions are unaffected by this fix, since they only
    /// ever look at stdout content whose order is stable (single pump per stream).
    /// </summary>
    [Fact]
    public void MergedJoin_PreservesOrderAndBounds()
    {
        var (lines, m1, m2) = StarvedPastM2Scenario();

        var cycle2 = WatchOutputSlicing.MergedJoin(lines, m1 + 1, m2);

        Assert.Contains("FAIL", cycle2);
        Assert.Contains("Insert_OnInsertReadsXRec_BuildsConcreteBeforeImage", cycle2);
        Assert.DoesNotContain(TimingNeedle, cycle2); // the starved stderr line is out of this window
    }

    /// <summary>
    /// THE MODE-2 PROOF (review round 3). The stdout m2 marker having appeared says nothing
    /// about whether the stderr pump's continuation for cycle 2's timing line has actually
    /// run yet — that line can simply not be in `lines` at all when the assertion samples it.
    /// Builds a list where cycle 1's line arrived normally (no starvation — this is NOT the
    /// #1843 shape), m2 has appeared, but cycle 2's own timing line has not been appended at
    /// all yet: only one GetSharedReferences match exists. The predicate WatchTests' waiter
    /// polls on must say "not yet" here — accepting this snapshot is exactly the "Sub-string
    /// not found" failure with a different root cause than the one #1843 fixed.
    /// </summary>
    [Fact]
    public void HasAtLeastWarmTimingMatches_ReturnsFalse_WhenOnlyCycle1sLineHasArrivedSoFar()
    {
        var lines = new List<CapturedLine>
        {
            new(OutputStream.Stdout, "PASS  Codeunit 60001 Insert_OnInsertReadsXRec_BuildsConcreteBeforeImage"),
            new(OutputStream.Stderr, "[emit-timing] GetSharedReferences (5 specs): 41000ms"),
            new(OutputStream.Stdout, WatchOutputSlicing.WaitingForSourceMarker + "… (Ctrl+C to quit)"), // m1
            new(OutputStream.Stdout, "[watch] change detected — re-running…"),
            new(OutputStream.Stdout, "FAIL  Codeunit 60001 Insert_OnInsertReadsXRec_BuildsConcreteBeforeImage"),
            new(OutputStream.Stdout, WatchOutputSlicing.WaitingForSourceMarker + "… (Ctrl+C to quit)"), // m2
            // Cycle 2's own GetSharedReferences line has NOT been written to `lines` at all
            // yet at this snapshot — the stderr pump's continuation simply hasn't run.
        };

        Assert.False(WatchOutputSlicing.HasAtLeastWarmTimingMatches(lines, 2));
        Assert.Equal(1, WatchOutputSlicing.CountWarmTimingMatches(lines));
    }

    /// <summary>
    /// Once both cycles' timing lines have actually been appended — however they got there,
    /// including the starved-past-both-markers shape from
    /// LastWarmTimingMs_FindsCycle2sTiming_EvenWhenCycle1sColdLineIsAlsoStarvedPastM1 — the
    /// predicate must say "yes", or the waiter would never terminate before its timeout.
    /// </summary>
    [Fact]
    public void HasAtLeastWarmTimingMatches_ReturnsTrue_OnceBothCyclesLinesHaveArrived()
    {
        var (lines, _, _) = StarvedPastM1AndM2Scenario();

        Assert.True(WatchOutputSlicing.HasAtLeastWarmTimingMatches(lines, 2));
        Assert.Equal(2, WatchOutputSlicing.CountWarmTimingMatches(lines));
    }

    // ── #1936 follow-up: the final-cycle window must exclude EARLIER burst cycles ──────
    //
    // A burst that CI load splits into two quiescence windows produces two markers. The
    // FIRST of those cycles ran against a half-applied tree and reports FAIL; the SECOND
    // ran against the settled tree and reports PASS. Slicing (afterIndex, lastMarker)
    // spans BOTH, so the phantom FAIL is inside the text the final-cycle assertions read.
    // FinalCycleStart must start the window after the second-to-last marker instead.

    /// <summary>
    /// Pre-burst cold cycle ends at m1. The burst then splits into two cycles: cycle A
    /// (phantom, FAIL against a half-applied tree) ending at mA, and cycle B (settled,
    /// PASS) ending at mB.
    /// </summary>
    private static (List<CapturedLine> lines, int m1, List<int> markers) SplitBurstScenario()
    {
        var lines = new List<CapturedLine>
        {
            new(OutputStream.Stdout, "PASS  Codeunit60210.Sum_OfAllValues_MatchesExpectedTotal"),
        };
        int m1 = lines.Count;
        lines.Add(new(OutputStream.Stdout, WatchOutputSlicing.WaitingForSourceMarker + "… (Ctrl+C to quit)"));

        lines.Add(new(OutputStream.Stdout, "[watch] change detected — re-running…"));
        lines.Add(new(OutputStream.Stdout, "FAIL  Codeunit60210.Sum_OfAllValues_MatchesExpectedTotal"));
        int mA = lines.Count;
        lines.Add(new(OutputStream.Stdout, WatchOutputSlicing.WaitingForSourceMarker + "… (Ctrl+C to quit)"));

        lines.Add(new(OutputStream.Stdout, "[watch] change detected — re-running…"));
        lines.Add(new(OutputStream.Stdout, "PASS  Codeunit60210.Sum_OfAllValues_MatchesExpectedTotal"));
        int mB = lines.Count;
        lines.Add(new(OutputStream.Stdout, WatchOutputSlicing.WaitingForSourceMarker + "… (Ctrl+C to quit)"));

        return (lines, m1, new List<int> { mA, mB });
    }

    [Fact]
    public void FinalCycleStart_SplitBurst_WindowExcludesTheEarlierPhantomFailCycle()
    {
        var (lines, m1, markers) = SplitBurstScenario();

        var start = WatchOutputSlicing.FinalCycleStart(markers, m1);
        var finalCycle = WatchOutputSlicing.MergedJoin(lines, start, markers[^1]);

        Assert.DoesNotContain("FAIL", finalCycle);
        Assert.Contains("PASS", finalCycle);
        Assert.Contains("Sum_OfAllValues_MatchesExpectedTotal", finalCycle);

        // The window must begin after the SECOND-TO-LAST marker, not after the pre-burst one.
        Assert.Equal(markers[^2] + 1, start);
    }

    [Fact]
    public void FinalCycleStart_SingleCycleBurst_WindowStartsAfterThePreBurstMarker()
    {
        var (lines, m1, markers) = SplitBurstScenario();
        var single = new List<int> { markers[^1] };

        var start = WatchOutputSlicing.FinalCycleStart(single, m1);

        // With one cycle there is no earlier burst cycle to exclude, so the window is the
        // whole span after the pre-burst marker — the pre-#1936 behaviour, unchanged.
        Assert.Equal(m1 + 1, start);
        Assert.Contains("PASS", WatchOutputSlicing.MergedJoin(lines, start, single[^1]));
    }

    [Fact]
    public void FinalCycleStart_NoMarkers_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WatchOutputSlicing.FinalCycleStart(new List<int>(), 0));
    }
}
