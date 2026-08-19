using System.Diagnostics;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #1904's own proof, in two parts split per #1936 (the same real-clock-dependence #1920 /
/// #1921 diagnosed and fixed in the sibling <c>WatchSourceTests</c> burst test, which this
/// file did not originally get converted along with):
///
/// The fixture (see Fixtures/WatchBurstSwitch) spreads a version switch over seven files:
/// six addend codeunits (F0..F5) and a Sum test whose expected total is a SEPARATE file
/// from every addend. "Version A" (all addends 1, expects 6) and "version B" (all addends
/// 10, expects 60) each pass on their own; any half-applied mix of the two files sums to
/// neither 6 nor 60. Under the pre-#1904 fixed `Thread.Sleep(250)` debounce, delivering the
/// seven writes with gaps below the quiet window released mid-burst — a wrong result,
/// reported as a real test failure, for a codeunit that is fine in both versions.
///
/// 1. <see cref="WaitForQuiescence_MirrorsBulkFileSwitchFixture_ReleasesOnlyAfterBurstSettles_Deterministic"/>
///    — the algorithmic claim ("exactly one cycle, never released mid-burst") driven
///    through <c>WatchSource.WatchActivity(nowTicks:)</c> / <c>WaitForQuiescence(nowTicks:,
///    sleep:)</c>'s injected virtual clock, mirroring this fixture's exact write shape (F0..F5
///    then Sum, 150ms gaps, default 250ms quiet window) with zero real elapsed time anywhere.
///    THIS is now the regression proof: it fails if quiescence reverts to a fixed
///    post-first-event debounce (verified by hand — see the PR description), and it cannot
///    flake under CI load because nothing in it depends on wall-clock time.
/// 2. <see cref="Watch_BulkFileSwitch_SettlesToCorrectResult"/> — kept on REAL time,
///    spawning the actual `--watch` process against a real `FileSystemWatcher`, because the
///    algorithmic proof above says nothing about whether the real subprocess wiring (process
///    spawn, real file events, re-emit, PASS/FAIL reporting) actually works end to end.
///
///    #1959: it no longer asserts PASS/FAIL on whichever cycle(s) the burst itself produced.
///    Real `FileSystemWatcher` event delivery can legitimately coalesce the whole burst into
///    ONE quiescence window (#1936/#1945 already established that CI load can split it into
///    several — #1959 is the mirror case). When that single window's own compile is triggered
///    by watcher events that don't 1:1 track every real write — inotify coalescing under load
///    is a known OS-level behaviour, not a runner bug — it can genuinely read a half-applied
///    tree and report a real FAIL for a codeunit that is fine in both versions, and there is no
///    SECOND cycle to fall back to: `WatchOutputSlicing.FinalCycleStart` on a single marker
///    necessarily returns that same cycle's own window (see its doc comment). No slicing
///    strategy over the burst's own output can distinguish that from an actual regression,
///    because both produce the byte-identical transcript — this is what sank #1945 and #1951,
///    each of which "fixed" a slicing detail the next real run went on to falsify. So the test
///    stops trying to read a verdict out of the burst's own uncontrollable timing.
///
///    What real time CAN still prove: end-to-end wiring works (the process starts, detects a
///    change, re-runs, reports a result) — proven by cycle 1 below, on an isolated single edit
///    with nothing else in flight, the same shape that has never itself been reported flaky —
///    plus one more such edit AFTER the burst has fully drained, once nothing else is
///    mid-cycle. That settling edit is not a retry of the burst assertion: it is a distinct,
///    deliberately uncontended write whose target file is, by construction, already sitting at
///    the fully-switched version B content on disk (the burst's own writes are synchronous and
///    long complete by the time it runs) — so the cycle it triggers reads the CURRENT tree
///    fresh, not whatever a phantom mid-burst cycle glimpsed. The PASS/FAIL claim on the
///    algorithmic "never release mid-burst" behaviour itself rests solely on test 1 above,
///    which cannot flake because nothing in it depends on wall-clock time; this test's own
///    PASS/FAIL claim rests on the settling edit's single, uncontended cycle.
/// </summary>
public class WatchBurstSwitchTests
{
    // #1936: the algorithmic "exactly one cycle" claim, driven through WatchSource's
    // injected virtual clock instead of the real process below — see the class doc comment.
    // Mirrors WatchBurstSwitchTests's own fixture write loop exactly: F0..F5 copied with a
    // 150ms delay after each, then Sum copied last with no further delay — i.e. seven events
    // at virtual t = 0, 150, 300, 450, 600, 750, 900ms, all comfortably below the default
    // 250ms quiet window between consecutive events (so a CORRECT quiescence implementation
    // must keep re-arming through the whole burst and release only once, after the last one).
    [Fact]
    public void WaitForQuiescence_MirrorsBulkFileSwitchFixture_ReleasesOnlyAfterBurstSettles_Deterministic()
    {
        const int quietMs = 250;
        var eventTimes = new long[] { 0, 150, 300, 450, 600, 750, 900 }; // F0, F1, F2, F3, F4, F5, Sum
        var lastEventTime = eventTimes[^1];

        long fakeNow = 0;
        Func<long> nowTicks = () => fakeNow;
        var activity = new AlRunner.WatchSource.WatchActivity(nowTicks);

        // Event #0 (F0) is delivered synchronously up front — models `signal.Wait()`
        // returning after the first real watcher event, exactly as WaitForSourceChange does
        // immediately before calling WaitForQuiescence in production.
        activity.Touch();
        var nextEventIndex = 1;

        void FakeSleep(int ms)
        {
            var target = fakeNow + ms;
            while (nextEventIndex < eventTimes.Length && eventTimes[nextEventIndex] <= target)
            {
                fakeNow = eventTimes[nextEventIndex]; // land exactly on the event's own virtual time
                activity.Touch();
                nextEventIndex++;
            }
            fakeNow = target;
        }

        AlRunner.WatchSource.WaitForQuiescence(
            activity, quietMs: quietMs, maxWaitMs: 10_000, nowTicks: nowTicks, sleep: FakeSleep);

        // The phantom-failure assertion: must not release before the burst's LAST event (Sum,
        // written last on purpose — see the fixture's own doc comment) — that is precisely
        // "started a cycle mid-switch". A reverted fixed-post-first-event debounce releases
        // ~250ms after F0 (virtual t=250), long before Sum's virtual t=900, and fails here.
        Assert.True(fakeNow >= lastEventTime,
            $"WaitForQuiescence returned at virtual {fakeNow}ms, BEFORE the burst's last event " +
            $"(Sum, written last) at {lastEventTime}ms — released mid-switch against a " +
            "half-applied tree (the #1904 phantom-failure bug: a fixed post-first-event " +
            "debounce cannot distinguish a settling save from an in-progress bulk switch).");

        // And it must not stall indefinitely either — one quiet window plus generous slack
        // after the last event, not the 10s cap.
        Assert.True(fakeNow <= lastEventTime + quietMs + 500,
            $"WaitForQuiescence returned {fakeNow - lastEventTime}ms after the burst's last " +
            "event — quiescence should release promptly once the burst settles, not stall " +
            "toward the cap.");
    }

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string FixtureRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "WatchBurstSwitch"));

    private const string TestName = "Sum_OfAllValues_MatchesExpectedTotal";

    [SkippableFact]
    public async Task Watch_BulkFileSwitch_SettlesToCorrectResult()
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

            // Drain whatever the burst produces — one cycle or several, PASS or a phantom
            // FAIL, it does not matter: #1959, none of that is a claim real time can prove
            // either way (see the class doc comment). This wait exists only so the settling
            // edit below starts from a quiet process, not to read a verdict out of it.
            var burstMarkers = await WaitForMarkersToSettle(m1, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(120));

            // The settling edit (#1959): one more write, made only after the burst has fully
            // drained and every burst file (F0..F5 and Sum) is therefore ALREADY sitting at
            // its final version B content on disk — the burst's own writes are synchronous
            // `File.Copy` calls in the loop above, long complete by this point. Re-copying
            // v2/Sum.Codeunit.al verbatim would hash-identical to what is already on disk and
            // trigger no recompile at all, so append a uniquely tagged trailing comment: same
            // runtime behaviour, a genuinely different file hash, so the watcher's single event
            // for it is unambiguous — nothing else is in flight — and the cycle it triggers
            // reads the CURRENT (fully version B) tree fresh, not whatever an earlier phantom
            // burst cycle may have glimpsed mid-switch.
            File.AppendAllText(Path.Combine(bundle, "Sum.Codeunit.al"),
                $"\n// settle-edit {Guid.NewGuid():N}\n");
            int settleMarker = await WaitForMarkerAfter(burstMarkers[^1] + 1, TimeSpan.FromSeconds(60));
            var settledCycle = Segment(burstMarkers[^1], settleMarker);
            Assert.Contains(TestName, settledCycle);
            Assert.DoesNotContain("FAIL", settledCycle);
            Assert.Contains("PASS", settledCycle);
        }
        finally
        {
            try { p.Kill(true); } catch { }
        }
    }
}
