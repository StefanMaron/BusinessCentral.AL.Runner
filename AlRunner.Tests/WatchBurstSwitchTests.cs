using System.Diagnostics;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #1904's own proof: drives the REAL `--watch` process (not the debounce loop in
/// isolation — WatchSourceTests covers that deterministically) through a bulk multi-file
/// switch that mimics a branch checkout, and asserts the phantom failure the issue reports
/// does NOT appear.
///
/// The fixture (see Fixtures/WatchBurstSwitch) spreads a version switch over seven files:
/// six addend codeunits (F0..F5) and a Sum test whose expected total is a SEPARATE file
/// from every addend. "Version A" (all addends 1, expects 6) and "version B" (all addends
/// 10, expects 60) each pass on their own; any half-applied mix of the two files sums to
/// neither 6 nor 60. Under the pre-fix fixed `Thread.Sleep(250)` debounce, delivering the
/// seven writes with gaps below the quiet window released mid-burst — a wrong result,
/// reported as a real test failure, for a codeunit that is fine in both versions — and
/// then picked the burst's tail up as a SECOND cycle once it settled: two cycles for one
/// switch, the first one a phantom. Quiescence must produce exactly ONE cycle for the whole
/// switch, and its result must be the correct, fully-settled version B PASS.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// </summary>
public class WatchBurstSwitchTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string FixtureRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "WatchBurstSwitch"));

    private const string TestName = "Sum_OfAllValues_MatchesExpectedTotal";

    [SkippableFact]
    public async Task Watch_BulkFileSwitch_ProducesExactlyOneCycle_AgainstSettledTree()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = Path.Combine(Path.GetTempPath(), "al-runner-watch-burst", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bundle);
        File.Copy(Path.Combine(FixtureRoot, "app.json"), Path.Combine(bundle, "app.json"));
        File.Copy(Path.Combine(FixtureRoot, "Assert.Codeunit.al"), Path.Combine(bundle, "Assert.Codeunit.al"));
        var v1Dir = Path.Combine(FixtureRoot, "v1");
        var v2Dir = Path.Combine(FixtureRoot, "v2");
        foreach (var f in Directory.GetFiles(v1Dir))
            File.Copy(f, Path.Combine(bundle, Path.GetFileName(f)));
        var cacheDir = Path.Combine(bundle, ".cache");

        // Same merged stdout/stderr capture shape as WatchTests.cs — see its header and
        // WatchOutputSlicing.cs for why two independent pumps only preserve WITHIN-stream
        // order, not cross-stream order (irrelevant here: every assertion below is
        // stdout-only, found via FindStdoutMarkerIndices).
        var lines = new List<CapturedLine>();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
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

        string ProcessLiveness() => p.HasExited ? $"process alive=false exit={p.ExitCode}" : "process alive=true";
        string DumpTail() { lock (lines) return string.Join("\n", lines.TakeLast(60).Select(l => $"[{l.Stream}] {l.Text}")); }

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

        // Poll until no NEW "waiting for AL source" marker has appeared for `settleWindow`
        // — i.e. the watch has genuinely gone idle again, not just momentarily between two
        // back-to-back cycles. Returns every marker index found after `afterIndex`, in
        // order: its COUNT is the number of watch cycles the burst produced, and the LAST
        // one delimits the final (should-be-correct) cycle's output.
        async Task<List<int>> WaitForMarkersToSettle(int afterIndex, TimeSpan settleWindow, TimeSpan overallTimeout)
        {
            var deadline = DateTime.UtcNow + overallTimeout;
            List<int> found = new();
            var lastGrowth = DateTime.UtcNow;
            var lastCount = 0;
            while (DateTime.UtcNow < deadline)
            {
                lock (lines)
                    found = WatchOutputSlicing.FindStdoutMarkerIndices(lines, WatchOutputSlicing.WaitingForSourceMarker, afterIndex + 1);
                if (found.Count != lastCount) { lastCount = found.Count; lastGrowth = DateTime.UtcNow; }
                else if (found.Count > 0 && DateTime.UtcNow - lastGrowth >= settleWindow)
                    return found;
                if (p.HasExited)
                {
                    await Task.Delay(500);
                    throw new TimeoutException(
                        $"subprocess exited early while waiting for cycles to settle ({ProcessLiveness()}).\n" +
                        $"--- last output ---\n{DumpTail()}");
                }
                await Task.Delay(300);
            }
            throw new TimeoutException(
                $"watch cycles never settled within {overallTimeout.TotalSeconds}s (found {found.Count} marker(s) " +
                $"so far). {ProcessLiveness()}\n--- last output ---\n{DumpTail()}");
        }

        string Segment(int from, int to) { lock (lines) return WatchOutputSlicing.MergedJoin(lines, from, to); }

        try
        {
            // Cycle 1 (cold): version A settles and passes (sum 6).
            int m1 = await WaitForMarkerAfter(0, TimeSpan.FromSeconds(150));
            var cycle1 = Segment(0, m1);
            Assert.Contains("PASS", cycle1);
            Assert.Contains(TestName, cycle1);
            Assert.DoesNotContain("FAIL  Codeunit", cycle1);

            // The burst switch: seven writes (F0..F5, then Sum LAST) with gaps well below
            // the 250ms default quiet window, spread over ~900ms total — the same shape as
            // #1904's reproduction (many events, gaps shorter than the debounce, total
            // burst longer than it). Sum is written LAST on purpose: while any addend has
            // already flipped to version B but Sum still expects version A's total (or vice
            // versa for a cycle that starts even later in the burst), the sum matches
            // NEITHER 6 nor 60 — a phantom mismatch that cannot occur once the whole switch
            // has settled.
            const int gapMs = 150;
            for (int i = 0; i < 6; i++)
            {
                File.Copy(Path.Combine(v2Dir, $"F{i}.Codeunit.al"), Path.Combine(bundle, $"F{i}.Codeunit.al"), overwrite: true);
                await Task.Delay(gapMs);
            }
            File.Copy(Path.Combine(v2Dir, "Sum.Codeunit.al"), Path.Combine(bundle, "Sum.Codeunit.al"), overwrite: true);

            // Let the watch run to completion and settle. Warm re-emits of this tiny
            // fixture are fast (milliseconds to low seconds per WatchTests.cs), so a 5s
            // quiet window comfortably distinguishes "no more cycles are coming" from
            // "the next cycle just hasn't finished yet", within a generous overall budget.
            var markers = await WaitForMarkersToSettle(m1, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(120));

            // The core #1904 claim: exactly ONE cycle for the whole burst, not two. Two
            // would mean a phantom cycle ran mid-switch (the old fixed-debounce bug) before
            // the correct, fully-settled cycle.
            Assert.True(markers.Count == 1,
                $"expected exactly 1 watch cycle for the whole burst switch, saw {markers.Count} " +
                "— a bulk multi-file rewrite must debounce to ONE cycle against the settled tree, " +
                "not fire early against a half-applied mix (the #1904 phantom-failure bug: two " +
                "cycles for one switch, the first against a tree that was still part-old / " +
                $"part-new).\n--- output between m1 and settle ---\n{Segment(m1 + 1, markers.Count > 0 ? markers[^1] : lines.Count)}");

            // And that one cycle must report the CORRECT, fully-settled version B result —
            // not a phantom failure for a test that is fine in both version A and version B.
            var finalCycle = Segment(m1 + 1, markers[0]);
            Assert.Contains(TestName, finalCycle);
            Assert.DoesNotContain("FAIL", finalCycle);
            Assert.Contains("PASS", finalCycle);
        }
        finally
        {
            try { p.Kill(true); } catch { }
        }
    }
}
