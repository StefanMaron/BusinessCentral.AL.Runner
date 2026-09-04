using System.Diagnostics;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #1905 (defect 4): when a `--watch` cycle falls back to a full rebuild (instead of
/// the proportional-cost incremental path), the reason must reach the console at
/// DEFAULT verbosity — not only under `--verbose`. A full rebuild costs whole minutes
/// on a large app (761-862s measured on NP Retail, #1905's own numbers), so which
/// reason forced it is a RESULT the developer needs, exactly like which BC version was
/// selected (Log.cs's `[bc]` history) and whether the expectations manifest was found
/// (`[expectations]`, #1984) — both were previously swallowed by the same
/// --verbose-only gate and both cost real, measured damage before being exempted.
///
/// Also proves the inverse the "judgment" half of #1905's ask cares about: the very
/// first --watch cycle for a bundle ALWAYS falls back (there is no baseline yet), and
/// that is not a fallback in any meaningful sense — printing an alarming "full
/// rebuild" line on every single startup would train the reader to ignore it, so cycle
/// 0 must stay quiet and only cycle 1+ is asserted here.
///
/// Runs the real CLI in non-interactive (piped) watch mode, so it exercises
/// Program.cs's plain-line fallback branch (WatchDashboard's own interactive-frame
/// surfacing is covered separately, and purely, in WatchDashboardTests — this test
/// proves the wiring that actually reaches Program.cs's console output end-to-end).
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// </summary>
public class WatchFullRebuildReasonTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string FixtureSrc = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "RecordTriggerXRec"));

    [SkippableFact]
    public async Task Watch_FullRebuildFallback_ReasonReachesDefaultVerbosityOutput_ButNotOnFirstCycle()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = Path.Combine(Path.GetTempPath(), "al-runner-watch-fullrebuild", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bundle);
        foreach (var f in Directory.GetFiles(FixtureSrc))
            File.Copy(f, Path.Combine(bundle, Path.GetFileName(f)));
        var testsCodeunitPath = Path.Combine(bundle, "XRecProbeTests.Codeunit.al");
        var cacheDir = Path.Combine(bundle, ".cache");

        var lines = new List<CapturedLine>();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            // Deliberately NO --verbose: this is the whole claim under test — the
            // reason must reach default-verbosity output.
            Arguments = TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg
                + $" \"{bundle}\" --watch --cache \"{cacheDir}\"",
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        using var p = Process.Start(psi)!;
        void Pump(StreamReader r, OutputStream stream) => Task.Run(async () =>
        {
            string? l;
            while ((l = await r.ReadLineAsync()) != null) lock (lines) lines.Add(new CapturedLine(stream, l));
        });
        Pump(p.StandardOutput, OutputStream.Stdout);
        Pump(p.StandardError, OutputStream.Stderr);

        string ProcessLiveness() =>
            p.HasExited ? $"process alive=false exit={p.ExitCode}" : "process alive=true";
        string DumpTail() { lock (lines) return string.Join("\n", lines.TakeLast(40).Select(l => $"[{l.Stream}] {l.Text}")); }
        // #2653: on ANY assertion failure below, dump every captured line with its list
        // index so the next occurrence answers "was the race index-vs-program-order (#1843
        // shape) or did the runner genuinely not report it" instead of requiring a fresh
        // reproduction — see WatchOutputSlicing.cs's header for the mechanism.
        string DumpAllIndexed() { lock (lines) return string.Join("\n", lines.Select((l, idx) => $"[{idx}][{l.Stream}] {l.Text}")); }

        async Task<int> WaitForMarkerAfter(int fromIndex, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                List<int> found;
                lock (lines)
                    found = WatchOutputSlicing.FindStdoutMarkerIndices(lines, WatchOutputSlicing.WaitingForSourceMarker, fromIndex);
                if (found.Count > 0) return found[0];
                if (p.HasExited)
                {
                    await Task.Delay(500);
                    throw new TimeoutException(
                        $"watch marker not seen — subprocess exited early ({ProcessLiveness()}).\n--- last output ---\n{DumpTail()}");
                }
                await Task.Delay(200);
            }
            if (p.HasExited) await Task.Delay(500);
            throw new TimeoutException($"watch marker not seen. {ProcessLiveness()}\n--- last output ---\n{DumpTail()}");
        }

        string Segment(int from, int to) { lock (lines) return WatchOutputSlicing.MergedJoin(lines, from, to); }

        // #2653: the FULL REBUILD reason line is written to STDERR, early in the cycle —
        // well before Emit/dispatch/test-execution/reporting run, all of which happen before
        // the cycle's OWN closing "waiting for AL source" marker on stdout (m2). Bounding the
        // search at m2 (the old `Segment(m1 + 1, m2)` shape) reproduces the exact #1843 race
        // for this marker: `lines`' index order is PUMP SCHEDULING order, not program order,
        // so a starved stderr pump can still land this line's list index past m2 even though
        // it was written, chronologically, long before it. Search unbounded forward from
        // fromIndex instead (mirrors LastWarmTimingMs/ContainsAfter — see
        // WatchOutputSlicing.cs), and POLL for it: m2 having appeared says nothing about
        // whether the stderr pump's continuation for THIS line has run yet (the #1843 "mode
        // 2" race — the line can simply not be in `lines` yet at all).
        async Task<string> WaitForTextAfter(int fromIndex, string text, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                string snapshot;
                lock (lines) snapshot = WatchOutputSlicing.MergedJoin(lines, fromIndex, lines.Count);
                if (snapshot.Contains(text)) return snapshot;
                if (DateTime.UtcNow >= deadline)
                {
                    if (p.HasExited) await Task.Delay(500);
                    throw new TimeoutException(
                        $"'{text}' not seen at/after index {fromIndex}. {ProcessLiveness()}\n" +
                        $"--- last output ---\n{DumpTail()}\n--- all lines ---\n{DumpAllIndexed()}");
                }
                await Task.Delay(200);
            }
        }

        try
        {
            // Cycle 0 (cold): the fixture always falls back here too ("no incremental
            // baseline yet") — but that fallback must NOT be reported as a "full
            // rebuild" alarm, since every single --watch invocation hits it.
            int m1 = await WaitForMarkerAfter(0, TimeSpan.FromSeconds(150));
            var cycle1 = Segment(0, m1);
            Assert.True(!cycle1.Contains("FULL REBUILD"),
                $"cycle1 (0..{m1}) unexpectedly contained FULL REBUILD.\n" +
                $"--- cycle1 ---\n{cycle1}\n--- all lines ---\n{DumpAllIndexed()}");

            // Force a genuine fallback on cycle 1 via an ACTUAL .al edit (app.json isn't
            // watched at all — WatchSource.cs filters strictly to "*.al", so an app.json-only
            // edit would never even trigger a cycle): append a second object declaration to
            // an already-tracked .al file. Per BcCompiler.Incremental.cs's
            // ClassifyDeclaredObject, the fast path requires exactly one object per file, so
            // this is a reliable, deterministic fallback with a specific, recognisable cause.
            var original = await File.ReadAllTextAsync(testsCodeunitPath);
            var edited = original + "\ncodeunit 60199 \"xRec Probe Extra RXT\"\n{\n}\n";
            await File.WriteAllTextAsync(testsCodeunitPath, edited);

            int m2 = await WaitForMarkerAfter(m1 + 1, TimeSpan.FromSeconds(240));

            // #2653: NOT `Segment(m1 + 1, m2)` — that upper bound reproduces the #1843 race
            // for this (stderr) line. Poll unbounded forward from m1 + 1 instead; `cycle2`
            // below is therefore "everything captured from the start of cycle 2 onward" (m2
            // itself, plus whatever arrived after it), not a window that closes at m2.
            var cycle2 = await WaitForTextAfter(m1 + 1, "FULL REBUILD", TimeSpan.FromSeconds(30));

            // The reason reached the console WITHOUT --verbose (the defect) and names
            // the specific cause, not a generic "something changed" — a reader must be
            // able to tell WHY the cycle cost minutes instead of milliseconds.
            Assert.True(cycle2.Contains("FULL REBUILD"),
                $"cycle2 (m1={m1} m2={m2}) did not contain FULL REBUILD.\n" +
                $"--- cycle2 (from {m1 + 1}) ---\n{cycle2}\n--- all lines ---\n{DumpAllIndexed()}");
            Assert.True(cycle2.Contains("XRecProbeTests.Codeunit.al"),
                $"cycle2 (m1={m1} m2={m2}) did not contain the touched file name.\n" +
                $"--- cycle2 (from {m1 + 1}) ---\n{cycle2}\n--- all lines ---\n{DumpAllIndexed()}");
            Assert.True(cycle2.Contains("declares 2 object(s)"),
                $"cycle2 (m1={m1} m2={m2}) did not contain the object-count reason.\n" +
                $"--- cycle2 (from {m1 + 1}) ---\n{cycle2}\n--- all lines ---\n{DumpAllIndexed()}");
        }
        finally
        {
            try { p.Kill(true); } catch { }
        }
    }
}
