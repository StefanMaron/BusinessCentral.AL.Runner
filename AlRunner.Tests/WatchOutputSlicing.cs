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
// pump-scheduling order, not write order across streams — only within a single stream is
// order preserved, because that stream has exactly one pump appending it.
//
// The test finds two stdout markers ("[watch] waiting for AL source…", written by
// Program.cs's watch loop via Console.WriteLine, Program.cs:1916) at indices m1 and m2, and
// used to assert that the *stderr* timing line BcCompiler.cs's `_mark` writes via
// Console.Error.WriteLine (BcCompiler.cs:1316, "[emit-timing] GetSharedReferences (…):
// <n>ms", fired at BcCompiler.cs:1354) lives inside the INDEX WINDOW [m1+1, m2). In program
// order the timing line is written strictly before the m2 marker — GetSharedReferences
// finishes and is timed well before the watch loop goes idle and prints "waiting for AL
// source" again for cycle 2. But list order is scheduling order: if the stderr pump's
// `ReadLineAsync` continuation is starved past the stdout pump's append of the m2 marker,
// the timing line lands at a list index >= m2 and a window bounded at `to` misses it even
// though it was written well within cycle 2. Nothing is lost; it is filed under the wrong
// cycle.
//
// The fix
// -------
// Track which stream produced each line, and stop bounding the stderr search at the next
// stdout marker. The claim under test ("the warm re-emit's symbol load was fast") only
// needs the diagnostic to have been written after cycle 2 started — not to sit below a
// stdout marker whose position relative to it is scheduling noise, not signal.
using System.Text;

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

    /// <summary>
    /// Indices of every stdout line containing <paramref name="marker"/>, in list order.
    /// Restricted to the stdout stream because the marker itself is only ever written to
    /// stdout — a stderr line that happens to contain the same substring must not count.
    /// </summary>
    public static List<int> FindStdoutMarkerIndices(IReadOnlyList<CapturedLine> lines, string marker)
    {
        var result = new List<int>();
        for (int i = 0; i < lines.Count; i++)
            if (lines[i].Stream == OutputStream.Stdout && lines[i].Text.Contains(marker))
                result.Add(i);
        return result;
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
    /// The text WatchTests searches to find a given cycle's warm-timing diagnostic.
    /// `to` (the index of the NEXT stdout marker) is intentionally NOT used to bound this
    /// search — see the file header for why: the diagnostic is on stderr, and a starved
    /// stderr-pump continuation can append it at a list index at or past `to` even though,
    /// in program order, it was written well inside the [from, to) cycle. Every stderr line
    /// appended from `from` onward — with no upper index bound — is fair game.
    /// </summary>
    public static string CycleTimingSearchText(IReadOnlyList<CapturedLine> lines, int from, int to)
    {
        var sb = new StringBuilder();
        for (int i = from; i < lines.Count; i++)
        {
            if (lines[i].Stream != OutputStream.Stderr) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(lines[i].Text);
        }
        return sb.ToString();
    }
}
