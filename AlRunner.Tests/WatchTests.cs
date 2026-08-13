using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Proves `--watch` re-runs IN-PROCESS and picks up a source edit on the next
/// cycle (the same-bundle reload working inside the resident watch process — the
/// gap that previously forced watch to spawn a child). We edit a table trigger so
/// the result flips PASS→FAIL on the second cycle; stale in-process state would
/// keep it passing.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// See DefineFlagIntegrationTests for why this used to be
/// [Collection("server-serial")] and no longer is — #1809.
/// </summary>
public class WatchTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string FixtureSrc = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "RecordTriggerXRec"));

    private static string CurrentFramework()
    {
        var v = Environment.Version;
        return $"net{v.Major}.{v.Minor}";
    }

    [SkippableFact]
    public async Task Watch_PicksUpEdit_InProcess_OnNextCycle()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = Path.Combine(Path.GetTempPath(), "al-runner-watch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bundle);
        foreach (var f in Directory.GetFiles(FixtureSrc))
            File.Copy(f, Path.Combine(bundle, Path.GetFileName(f)));
        var tablePath = Path.Combine(bundle, "XRecProbe.Table.al");
        var cacheDir = Path.Combine(bundle, ".cache");

        // Merges stdout and stderr into ONE list via two independent fire-and-forget pumps
        // below — list order is pump-scheduling order, not cross-stream write order. Each
        // entry therefore records which stream it came from (CapturedLine), so cycle
        // slicing can tell "written late" apart from "written out of order" — see
        // WatchOutputSlicing.cs's header (#1843) for the full mechanism.
        var lines = new List<CapturedLine>();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg
                + $" \"{bundle}\" --watch --cache \"{cacheDir}\"",
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        psi.Environment["BCCOMPILER_TIMING"] = "1"; // emit GetSharedReferences timing lines
        using var p = Process.Start(psi)!;
        void Pump(StreamReader r, OutputStream stream) => Task.Run(async () =>
        {
            string? l;
            while ((l = await r.ReadLineAsync()) != null) lock (lines) lines.Add(new CapturedLine(stream, l));
        });
        Pump(p.StandardOutput, OutputStream.Stdout);
        Pump(p.StandardError, OutputStream.Stderr);

        // A marker that never shows up is ambiguous on its own: it's byte-identical
        // whether the watcher armed-but-never-fired OR the subprocess quietly died while
        // idling (a killed child produces the same "marker, then silence" shape as a deaf
        // watcher). Neither this timeout message nor the loop used to distinguish the two
        // — see #1822 discussion: don't let a future occurrence turn into another round of
        // speculation about which one happened. p.HasExited/p.ExitCode settle it directly,
        // and checking it on every poll also fails fast (no need to burn the whole budget)
        // when the process is already gone.
        string ProcessLiveness() =>
            p.HasExited ? $"process alive=false exit={p.ExitCode}" : "process alive=true";

        string DumpTail() => string.Join("\n", lines.TakeLast(40).Select(l => $"[{l.Stream}] {l.Text}"));

        async Task<int> WaitForMarkerAfter(int fromIndex, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                List<int> found;
                lock (lines)
                    found = WatchOutputSlicing.FindStdoutMarkerIndices(
                        lines, WatchOutputSlicing.WaitingForSourceMarker, fromIndex);
                if (found.Count > 0) return found[0];
                if (p.HasExited)
                {
                    // Pump is fire-and-forget (Task.Run, result discarded): pipe output
                    // the exiting process already wrote can still be in flight when
                    // HasExited flips true, so a capture taken right here can truncate
                    // exactly the lines that would explain the exit. Give the pump
                    // tasks a moment to drain before dumping — this diagnostic exists
                    // so a real occurrence is self-explanatory, not half-explanatory.
                    await Task.Delay(500);
                    string exitedDump; lock (lines) exitedDump = DumpTail();
                    throw new TimeoutException(
                        $"watch marker not seen — subprocess exited early ({ProcessLiveness()}).\n" +
                        $"--- last output ---\n{exitedDump}");
                }
                await Task.Delay(200);
            }
            if (p.HasExited) await Task.Delay(500); // same drain guard for the deadline path below
            string dump; lock (lines) dump = DumpTail();
            throw new TimeoutException(
                $"watch marker not seen. {ProcessLiveness()}\n--- last output ---\n{dump}");
        }

        // Bounded [from, to) window, both streams merged — still correct for the
        // PASS/FAIL/fixture-name assertions below, which only ever look for stdout content
        // whose relative order is unaffected by the cross-stream race (see WatchOutputSlicing.cs).
        string Segment(int from, int to)
        {
            lock (lines) return WatchOutputSlicing.MergedJoin(lines, from, to);
        }

        // Reaching the stdout m2 marker proves cycle 2 finished; it proves nothing about
        // whether the STDERR pump's continuation for cycle 2's GetSharedReferences line has
        // run yet — that is a second, independent race from the one WatchOutputSlicing's
        // last-match logic closes (see its file header, "mode 2"). Poll for the evidence
        // instead of reading a snapshot the instant m2 shows up: wait for an ABSOLUTE count
        // of `minCount` stderr matches, not a delta, so a cycle-1 line that is itself starved
        // past m1 cannot be mistaken for cycle 2's arrival.
        async Task WaitForWarmTimingCount(int minCount, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                bool have;
                lock (lines) have = WatchOutputSlicing.HasAtLeastWarmTimingMatches(lines, minCount);
                if (have) return;
                if (p.HasExited)
                {
                    await Task.Delay(500); // same drain guard as WaitForMarkerAfter
                    int exitedCount; string exitedDump;
                    lock (lines)
                    {
                        exitedCount = WatchOutputSlicing.CountWarmTimingMatches(lines);
                        exitedDump = WatchOutputSlicing.StderrText(lines);
                    }
                    throw new TimeoutException(
                        $"only {exitedCount} GetSharedReferences line(s) captured — subprocess exited early " +
                        $"({ProcessLiveness()}).\n--- stderr ---\n{exitedDump}");
                }
                await Task.Delay(200);
            }
            int finalCount; string dump;
            lock (lines)
            {
                finalCount = WatchOutputSlicing.CountWarmTimingMatches(lines);
                dump = WatchOutputSlicing.StderrText(lines);
            }
            throw new TimeoutException(
                $"only {finalCount} GetSharedReferences line(s) captured after {timeout.TotalSeconds}s " +
                $"(need {minCount}). {ProcessLiveness()}\n--- stderr ---\n{dump}");
        }

        try
        {
            // Cycle 1 (cold): the fixture test passes (Counter 0 -> 1, asserts '1').
            int m1 = await WaitForMarkerAfter(0, TimeSpan.FromSeconds(150));
            var cycle1 = Segment(0, m1);
            Assert.Contains("PASS", cycle1);
            Assert.Contains("Insert_OnInsertReadsXRec_BuildsConcreteBeforeImage", cycle1);
            Assert.DoesNotContain("FAIL  Codeunit", cycle1);

            // Edit ONLY the table trigger (+1 -> +9). The test still asserts '1', so
            // the next cycle MUST now FAIL — proving the edited table was reloaded
            // in-process (stale state would keep it passing).
            var table = await File.ReadAllTextAsync(tablePath);
            var edited = table.Replace("xRec.\"Counter\" + 1", "xRec.\"Counter\" + 9");
            Assert.NotEqual(table, edited);
            await File.WriteAllTextAsync(tablePath, edited);

            // Cycle 2 (warm, after the edit).
            //
            // This budget is only "did the cycle finish at all" — it is NOT the warm-vs-cold
            // claim, which the GetSharedReferences timing assertion below makes on its own. So
            // it should be generous: at 60s this was the single flaky test in the suite, passing
            // in 57s when run alone and timing out when the full 193-test suite had just driven
            // the machine through several BC engine boots. A cycle that has genuinely gone cold
            // still fails loudly on the <5s assertion below, so a longer wait here costs nothing
            // and removes a false red that trains people to re-run and shrug.
            int m2 = await WaitForMarkerAfter(m1 + 1, TimeSpan.FromSeconds(240));
            var cycle2 = Segment(m1 + 1, m2);
            Assert.Contains("FAIL", cycle2);
            Assert.Contains("Insert_OnInsertReadsXRec_BuildsConcreteBeforeImage", cycle2);
            // The dependency loader stayed warm in-process across the edit: the
            // re-emit's symbol load is near-instant, not a cold ~40s reload.
            //
            // Assert the MAGNITUDE, not the exact string. This used to be
            // Assert.Contains("0ms"), which demanded the step round to exactly zero
            // milliseconds — on a Release build or a loaded machine it reports "1ms"
            // and the test failed despite the warm path working perfectly. The real
            // claim is warm (milliseconds) vs cold (tens of seconds), so a generous
            // ceiling still fails loudly if the loader ever goes cold here.
            //
            // Deliberately NOT scoped to `cycle2` (the stdout-marker-bounded window), and NOT
            // scoped by index at all — this diagnostic is on STDERR (BcCompiler.cs's
            // `_mark`), merged into `lines` via a pump independent from the stdout pump that
            // positions m1/m2. List order is pump-scheduling order, not cross-stream write
            // order, so a starved stderr pump continuation can append EITHER cycle's timing
            // line on the wrong side of EITHER stdout marker. Cycle 1 also writes a
            // GetSharedReferences line (its ~40s cold reload), so an index window bounded
            // only below (at m1+1) is exposed to the identical race, mirrored.
            //
            // Stderr itself has exactly one pump, so stderr-internal order IS write order:
            // cycle 1's line is always before cycle 2's. With exactly two cycles, cycle 2's
            // timing is therefore always the LAST GetSharedReferences match in the entirely
            // unbounded stderr stream — no index window, in either direction. See
            // WatchOutputSlicing.cs's header — #1843 — for the full mechanism and the
            // deterministic synthetic-sequence proof in WatchOutputSlicingTests.
            //
            // But reaching m2 only proves the STDOUT pump saw cycle 2 finish — it says
            // nothing about whether the STDERR pump's continuation for cycle 2's timing line
            // has run at all yet. Wait for that evidence explicitly (10s is generous: the
            // line is written seconds before m2 in program order) before reading `lines`,
            // instead of sampling it the instant m2 appears.
            await WaitForWarmTimingCount(2, TimeSpan.FromSeconds(10));
            string stderrText;
            lock (lines) stderrText = WatchOutputSlicing.StderrText(lines);
            Assert.Contains("GetSharedReferences", stderrText);
            // The label carries a parenthetical, e.g.
            //   [emit-timing] GetSharedReferences (5 specs): 787ms
            int? elapsedMsOpt;
            lock (lines) elapsedMsOpt = WatchOutputSlicing.LastWarmTimingMs(lines);
            Assert.True(elapsedMsOpt.HasValue,
                $"expected an '[emit-timing] GetSharedReferences: <n>ms' line, got:\n{stderrText}");
            var elapsedMs = elapsedMsOpt!.Value;
            Assert.True(elapsedMs < 5_000,
                $"warm in-process symbol load took {elapsedMs}ms — a warm re-emit must not pay " +
                "the cold ~40s dependency reload. The loader did not stay warm across the edit.");
        }
        finally
        {
            try { p.Kill(true); } catch { }
        }
    }
}
