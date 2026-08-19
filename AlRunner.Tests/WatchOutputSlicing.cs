// WatchOutputSlicing — the merge/slice classification logic WatchTests uses to find a
// diagnostic line inside a specific watch cycle, factored out so #1843's ordering bug can
// be proven and fixed against a synthetic, deterministic line sequence instead of a
// scheduling race against a live subprocess.
//
// The bug (#1843)
// ----------------
// WatchTests.Watch_PicksUpEdit_InProcess_OnNextCycle spawns `--watch` and merges the
// child's stdout and stderr into one list via two INDEPENDENT fire-and-forget pumps (one
// per stream), each appending under a shared lock as lines arrive. List order is therefore
// pump-scheduling order, not write order across streams — only WITHIN a single stream is
// order preserved, because that stream has exactly one pump appending it.
//
// The test finds two stdout markers ("[watch] waiting for AL source…", written by
// Program.cs's watch loop via Console.WriteLine, Program.cs:1916) at indices m1 and m2, and
// originally asserted that the *stderr* timing line BcCompiler.cs's `_mark` writes via
// Console.Error.WriteLine (BcCompiler.cs:1316, "[emit-timing] GetSharedReferences (…):
// <n>ms", fired at BcCompiler.cs:1354) lives inside the INDEX WINDOW [m1+1, m2). In program
// order the timing line is written strictly before the m2 marker — GetSharedReferences
// finishes and is timed well before the watch loop goes idle and prints "waiting for AL
// source" again for cycle 2. But list order is scheduling order: if the stderr pump's
// `ReadLineAsync` continuation is starved past the stdout pump's append of the m2 marker,
// the timing line lands at a list index >= m2 and a window bounded above misses it even
// though it was written well within cycle 2.
//
// The first fix attempt dropped the upper bound but kept a lower bound at m1+1 — which is
// exposed to the SAME race, mirrored: cycle 1 is the cold cycle and also writes a
// GetSharedReferences line (tens of seconds), and if THAT line's pump continuation is
// starved past m1, it lands inside the "unbounded forward from m1+1" scan and gets read as
// cycle 2's timing, failing the <5000ms assertion for the wrong reason.
//
// The actual fix
// ---------------
// Both timing lines are on stderr, and stderr has exactly one pump, so stderr-internal
// order is guaranteed regardless of how either pump is scheduled relative to the OTHER
// stream: cycle 1's GetSharedReferences line is always before cycle 2's in the list. There
// are exactly two watch cycles in this test, so cycle 2's line is simply the LAST
// GetSharedReferences match in the (entirely unbounded) stderr stream — no index window at
// all, in either direction. A cycle-1 line starved past m1 is still not the last match; a
// cycle-2 line starved past m2 is still found, because nothing bounds the scan above.
//
// A second, distinct failure mode (review round 3)
// --------------------------------------------------
// LastWarmTimingMs answers "what does `lines` say right now" — it is a pure function over a
// snapshot. Nothing about reading that snapshot guarantees cycle 2's stderr line has been
// APPENDED yet by the time the caller reads it. WatchTests' cycle-2 assertions used to run
// the instant the stdout m2 marker appeared, with no synchronization between the stdout pump
// (which just posted m2) and the stderr pump (which may not have run its next continuation
// at all). That is a second race, not a variant of the first: the line isn't misfiled, it
// simply isn't in `lines` yet. Both modes produce the BYTE-IDENTICAL
// "Assert.Contains() Failure ... Not found: GetSharedReferences" transcript, so a failure log
// alone cannot tell you which one fired — "the fix" only closes mode 1.
//
// The fix for mode 2 is to wait for the evidence instead of sampling for it: poll `lines`
// until the stderr stream contains AT LEAST TWO GetSharedReferences matches (one per cycle),
// THEN take the last. Waiting for an absolute count, not a delta from a snapshot taken at m1,
// matters: a delta-from-m1 approach reads 0 at m1 if cycle 1's own line is *also* starved
// past m1, and then accepts cycle 1's own late arrival as "the count increased" — reintroducing
// the exact bug LastWarmTimingMs was written to close. HasAtLeastWarmTimingMatches below is
// that predicate; the polling loop that uses it lives in WatchTests.cs (WaitForWarmTimingCount)
// since it needs the live process's cancellation/timeout plumbing, but the predicate itself is
// unit-tested here.
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace AlRunner.Tests;

public enum OutputStream
{
    Stdout,
    Stderr,
}

public readonly record struct CapturedLine(OutputStream Stream, string Text);

public static class WatchOutputSlicing
{
    public const string WaitingForSourceMarker = "[watch] waiting for AL source";
    private const string WarmTimingPattern = @"GetSharedReferences[^:]*:\s*(\d+)ms";

    /// <summary>
    /// Indices, from <paramref name="fromIndex"/> onward, of every stdout line containing
    /// <paramref name="marker"/>, in list order. Restricted to the stdout stream because the
    /// marker itself is only ever written to stdout — a stderr line that happens to contain
    /// the same substring must not count. This is what WatchTests' WaitForMarkerAfter polls
    /// on the real process's captured output.
    /// </summary>
    public static List<int> FindStdoutMarkerIndices(IReadOnlyList<CapturedLine> lines, string marker, int fromIndex = 0)
    {
        var result = new List<int>();
        for (int i = fromIndex; i < lines.Count; i++)
            if (lines[i].Stream == OutputStream.Stdout && lines[i].Text.Contains(marker))
                result.Add(i);
        return result;
    }

    /// <summary>
    /// Start index (inclusive) of the LAST watch cycle among <paramref name="markers"/>.
    /// Each marker ENDS a cycle, so the final cycle begins just after the SECOND-TO-LAST
    /// marker. Only when the burst produced a single cycle does it begin just after
    /// <paramref name="afterIndex"/> — the marker that ended the preceding, pre-burst cycle.
    ///
    /// #1936 dropped WatchBurstSwitchTests' `markers.Count == 1` assertion so that a burst
    /// which CI load legitimately splits into several quiescence windows still passes. That
    /// relaxation is defeated by slicing from <paramref name="afterIndex"/> to the LAST
    /// marker: the window then spans EVERY cycle the burst produced, so an early mid-burst
    /// cycle's phantom FAIL lands inside the text the final-cycle assertions read, and the
    /// test fails for precisely the load-dependent reason the relaxation exists to tolerate.
    /// Observed on the BC 27.5 leg of PR #1949: "Assert.DoesNotContain() Failure: Sub-string
    /// found ... FAIL  Codeunit60210".
    /// </summary>
    public static int FinalCycleStart(IReadOnlyList<int> markers, int afterIndex)
    {
        if (markers.Count == 0)
            throw new ArgumentOutOfRangeException(nameof(markers), "no cycle markers — cannot delimit a final cycle");
        return markers.Count >= 2 ? markers[^2] + 1 : afterIndex + 1;
    }

    /// <summary>
    /// Merged text of both streams within the stdout-marker-delimited index window
    /// [from, to) — still valid for the PASS/FAIL/fixture-name assertions, which only ever
    /// look for stdout content, and whose relative order is unaffected by the cross-stream
    /// race (a single stream has exactly one pump, so its own line order is preserved).
    /// </summary>
    public static string MergedJoin(IReadOnlyList<CapturedLine> lines, int from, int to)
    {
        var sb = new StringBuilder();
        for (int i = from; i < to && i < lines.Count; i++)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(lines[i].Text);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Every stderr line, in list order, newline-joined — NO index bounds, in either
    /// direction. Stderr has exactly one pump, so this order is exactly write order for
    /// that stream; it is only the position of stderr lines RELATIVE TO stdout lines that
    /// scheduling can scramble, and this function never looks at stdout at all.
    /// </summary>
    public static string StderrText(IReadOnlyList<CapturedLine> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.Stream != OutputStream.Stderr) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line.Text);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The warm-timing value (milliseconds) WatchTests asserts for cycle 2, or null if no
    /// "[emit-timing] GetSharedReferences (…): &lt;n&gt;ms" line was written at all.
    ///
    /// Takes the LAST match across the entire (unbounded) stderr stream, not the first and
    /// not an index-windowed one. There are exactly two watch cycles in this test, each
    /// writing exactly one GetSharedReferences line, in program order cycle-1-then-cycle-2 —
    /// and because stderr has exactly one pump, that program order is exactly list order.
    /// The last match is therefore always cycle 2's, independent of whether either line's
    /// pump continuation got scheduled early or late relative to the stdout m1/m2 markers.
    /// </summary>
    public static int? LastWarmTimingMs(IReadOnlyList<CapturedLine> lines)
    {
        var matches = Regex.Matches(StderrText(lines), WarmTimingPattern);
        return matches.Count > 0 ? int.Parse(matches[^1].Groups[1].Value) : null;
    }

    /// <summary>
    /// How many GetSharedReferences lines have been captured on stderr so far, across the
    /// whole (unbounded) stream. Cycle 1 writes exactly one (the cold reload), cycle 2
    /// writes exactly one (the warm re-emit) — so this reaches 2 once, and only once, both
    /// cycles' diagnostics have actually been appended to `lines`.
    /// </summary>
    public static int CountWarmTimingMatches(IReadOnlyList<CapturedLine> lines) =>
        Regex.Matches(StderrText(lines), WarmTimingPattern).Count;

    /// <summary>
    /// The predicate WatchTests polls on before trusting <see cref="LastWarmTimingMs"/>:
    /// has cycle 2's timing line actually arrived yet? Reading `lines` the instant the
    /// stdout m2 marker appears answers "what does the snapshot say right now", not "has the
    /// stderr pump's continuation for cycle 2's line run yet" — those are different
    /// questions, and conflating them is the second failure mode described in the file
    /// header (mode 2: line absent, not misfiled). Checking an ABSOLUTE count rather than a
    /// delta from a count snapshotted at m1 matters: a delta check reads 0 at m1 whenever
    /// cycle 1's own line is starved past m1 too, and then wrongly accepts cycle 1's late
    /// arrival as "cycle 2's line showed up".
    /// </summary>
    public static bool HasAtLeastWarmTimingMatches(IReadOnlyList<CapturedLine> lines, int minCount) =>
        CountWarmTimingMatches(lines) >= minCount;
}
