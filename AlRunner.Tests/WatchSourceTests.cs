// WatchSourceTests — deterministic proof for #1822's arm-before-announce contract.
//
// WatchTests.Watch_PicksUpEdit_InProcess_OnNextCycle (spawns the real runner, polls the
// console for the "waiting for AL source changes" marker on a 200ms loop, then edits the
// fixture) is a flaky reproduction of the race, not a proof: whether it goes red depends
// on OS scheduling luck between Console.Out.Flush() and EnableRaisingEvents = true. These
// tests instead encode the ordering CONTRACT directly — onArmed is invoked by
// WatchSource itself, only once every FileSystemWatcher is already live — so an edit made
// from *inside* onArmed is the earliest possible moment a real editor could race the
// watcher, and it must always be seen. No polling, no console output, no child process.
using Xunit;

namespace AlRunner.Tests;

public sealed class WatchSourceTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-watchsource-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Reach the internal WatchSource type via reflection-free direct reference: the type
    // is `internal` in namespace AlRunner, and AlRunner.csproj declares
    // InternalsVisibleTo("AlRunner.Tests"), so a plain `AlRunner.WatchSource` reference
    // compiles directly — no reflection needed. (Comment kept because the AL-runner
    // pattern elsewhere in this suite reaches internals via reflection; this one doesn't
    // need to.)

    [Fact]
    public async Task WaitForSourceChange_EditMadeFromInsideOnArmed_IsSeen()
    {
        // This is the exact #1822 race, made deterministic: onArmed is the earliest
        // possible instant an external editor could act on "now watching". If the
        // implementation ever regresses to announcing before arming, an edit made here
        // races (and can lose to) EnableRaisingEvents = true and the wait below times out.
        var dir = NewTempDir();
        var file = Path.Combine(dir, "Some.Table.al");
        File.WriteAllText(file, "table 60000 Some { }");
        bool onArmedRan = false;

        var task = Task.Run(() => AlRunner.WatchSource.WaitForSourceChange(
            new List<string> { dir },
            onArmed: () =>
            {
                onArmedRan = true;
                File.WriteAllText(file, "table 60000 Some { fields { field(1; A; Integer) { } } }");
            }));

        var winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.True(onArmedRan, "onArmed must run on the success path.");
        Assert.True(ReferenceEquals(task, winner),
            "WaitForSourceChange did not observe the edit written from inside onArmed within " +
            "15s — this means onArmed ran before the watchers were live (the #1822 regression). " +
            "(Not awaiting the hung task further to avoid blocking the test run.)");
        Assert.True(await task, "WaitForSourceChange must report a change was detected.");
    }

    [Fact]
    public void ArmSourceWatch_NoExistingSourceDir_ReturnsNull_AndDoesNotInvokeOnArmed()
    {
        // A bundle path that resolves to nothing on disk: no app.json to climb to, and the
        // bundle directory itself does not exist either.
        var missing = Path.Combine(Path.GetTempPath(), "al-runner-watchsource-tests", "does-not-exist-" + Guid.NewGuid().ToString("N"));
        bool onArmedRan = false;

        var savedErr = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        (System.Threading.ManualResetEventSlim Signal, List<FileSystemWatcher> Watchers, AlRunner.WatchSource.WatchActivity Activity)? armed;
        try
        {
            armed = AlRunner.WatchSource.ArmSourceWatch(
                new List<string> { missing },
                onArmed: () => onArmedRan = true);
        }
        finally
        {
            Console.SetError(savedErr);
        }

        Assert.Null(armed);
        Assert.False(onArmedRan, "onArmed must NOT run on the nothing-to-watch path.");
        Assert.Contains("[watch] no source directories to watch.", captured.ToString());
    }

    [Fact]
    public void ArmSourceWatch_ExistingSourceDir_ReturnsWatchers_AndRunsOnArmedExactlyOnce()
    {
        var dir = NewTempDir();
        int onArmedCount = 0;

        var armed = AlRunner.WatchSource.ArmSourceWatch(
            new List<string> { dir },
            onArmed: () => onArmedCount++);

        try
        {
            Assert.NotNull(armed);
            var (signal, watchers, _) = armed!.Value;
            Assert.NotEmpty(watchers);
            Assert.All(watchers, w => Assert.True(w.EnableRaisingEvents));
            Assert.Equal(1, onArmedCount);
            Assert.False(signal.IsSet);
        }
        finally
        {
            if (armed != null)
                foreach (var w in armed.Value.Watchers) { w.EnableRaisingEvents = false; w.Dispose(); }
            armed?.Signal.Dispose();
        }
    }

    // #1904: a fixed `Thread.Sleep(250)` after only the FIRST watcher event cannot tell
    // "one save, done" apart from "checkout in progress, more events coming" — it releases
    // 250ms after the first event regardless. A branch switch / bulk rewrite delivers many
    // file events spread over seconds, so the old debounce started a cycle mid-checkout,
    // against a tree that was still part-old / part-new.
    //
    // #1920: this test USED to reproduce that shape with real `Task.Delay(120)` writes
    // against a real FileSystemWatcher, asserting on real `Stopwatch` timings. That is what
    // flaked red on the 27.3/28.3/28.4 CI legs — not a quiescence-logic regression. Root
    // cause: a nominal 120ms gap between two writes is only "below the 250ms quiet window"
    // if the CI machine keeps `Task.Delay(120)` honest; under load a single gap can stretch
    // past 250ms, at which point a CORRECT quiescence implementation legitimately concludes
    // "quiet" and releases early relative to the test's real-time assumption — this is
    // indistinguishable, from a bare "returned at 250ms" symptom, from the actual #1904
    // fixed-debounce bug the test exists to catch. A real-clock test cannot tell those two
    // causes apart, which is exactly why it must not be trusted to prove or disprove either.
    //
    // The fix is to stop depending on real time entirely: drive WaitForQuiescence with an
    // injected virtual clock (WatchActivity(nowTicks:) / WaitForQuiescence(nowTicks:,
    // sleep:)) so the burst's timing is scripted exactly, not subject to scheduler jitter.
    // `sleep` fast-forwards the virtual clock by the requested amount and, along the way,
    // delivers any scheduled event whose virtual timestamp falls inside that interval —
    // modeling the real algorithm's periodic poll-and-check without any wall-clock wait, so
    // this test runs in microseconds and cannot flake regardless of machine load.
    [Fact]
    public void WaitForQuiescence_BurstBelowQuietWindow_ReleasesOnlyAfterBurstSettles_Deterministic()
    {
        const int fileCount = 12;
        const int gapMs = 120;
        const int quietMs = 250;
        Assert.True(gapMs < quietMs,
            "the reproduction depends on each event re-arming quiescence — the gap must be " +
            "smaller than the quiet window.");

        // The burst: 12 events, gapMs apart, at virtual times 0, 120, 240, ..., 1320.
        var eventTimes = new List<long>();
        for (int i = 0; i < fileCount; i++) eventTimes.Add(i * (long)gapMs);
        var lastEventTime = eventTimes[^1];

        long fakeNow = 0;
        Func<long> nowTicks = () => fakeNow;
        var activity = new AlRunner.WatchSource.WatchActivity(nowTicks);

        // Event #0 is delivered synchronously up front — this models `signal.Wait()`
        // returning after the first real watcher event, which is what the production
        // caller (WaitForSourceChange) does immediately before calling WaitForQuiescence.
        activity.Touch();
        var nextEventIndex = 1;

        void FakeSleep(int ms)
        {
            var target = fakeNow + ms;
            while (nextEventIndex < eventTimes.Count && eventTimes[nextEventIndex] <= target)
            {
                fakeNow = eventTimes[nextEventIndex]; // land exactly on the event's own virtual time
                activity.Touch();
                nextEventIndex++;
            }
            fakeNow = target;
        }

        AlRunner.WatchSource.WaitForQuiescence(
            activity, quietMs: quietMs, maxWaitMs: 10_000, nowTicks: nowTicks, sleep: FakeSleep);

        // The phantom-failure assertion: must not release before the burst's LAST event —
        // that is precisely "started a cycle mid-checkout". Fails under the old fixed
        // Thread.Sleep(250), which (simulated the same way — see the RED-check note below)
        // returns ~250ms after the FIRST event, ~1070ms before the last one here.
        Assert.True(fakeNow >= lastEventTime,
            $"WaitForQuiescence returned at virtual {fakeNow}ms, BEFORE the burst's last event " +
            $"at {lastEventTime}ms — released mid-burst against a half-applied tree (the #1904 " +
            "phantom-failure bug: a fixed post-first-event debounce cannot distinguish a " +
            "settling save from an in-progress bulk rewrite).");

        // And it must not stall indefinitely either — a single quiet window plus generous
        // slack after the last event, not the 10s cap.
        Assert.True(fakeNow <= lastEventTime + quietMs + 500,
            $"WaitForQuiescence returned {fakeNow - lastEventTime}ms after the burst's last " +
            "event — quiescence should release promptly once the burst settles, not stall " +
            "toward the cap.");

        // RED-check (acceptance criterion for #1920): reverting WaitForQuiescence to a fixed
        // post-first-event debounce must make the first assertion above fail. Verified by
        // hand — not asserted here as executable code, since there is no non-hacky way to
        // swap the algorithm at runtime without reintroducing the very bug this proves
        // against — by temporarily replacing the method body with
        // `doSleep((int)quiet); return;` and re-running this test: it fails with
        // "returned at virtual 250ms, BEFORE the burst's last event at 1320ms", confirming
        // the test actually discriminates the two implementations. See the PR description
        // for the transcript.
    }

    // The companion negative-latency check: a SINGLE save (no burst) must still release
    // close to one quiet window, not be delayed by the quiescence machinery itself. This is
    // the "must not make the common case feel worse" requirement from #1904 — a fix that
    // only ever waited for the 10s cap would "solve" the phantom failure by making every
    // save feel broken.
    [Fact]
    public async Task WaitForSourceChange_SingleSave_ReleasesWithinOneQuietWindow()
    {
        var dir = NewTempDir();
        var file = Path.Combine(dir, "Single.Table.al");
        var sw = new System.Diagnostics.Stopwatch();

        var task = Task.Run(() => AlRunner.WatchSource.WaitForSourceChange(
            new List<string> { dir },
            onArmed: () =>
            {
                sw.Start();
                File.WriteAllText(file, "table 60001 Single { }");
            }));

        var winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.True(ReferenceEquals(task, winner), "a single save must not approach the 10s cap.");
        Assert.True(await task);
        var elapsedMs = sw.ElapsedMilliseconds;

        Assert.True(elapsedMs <= AlRunner.WatchSource.QuietMs + 2_000,
            $"a single save took {elapsedMs}ms to release — quiescence must not add noticeable " +
            $"latency beyond one quiet window ({AlRunner.WatchSource.QuietMs}ms) for the common case.");
    }

    // #1904's second exposure: a watcher-buffer overflow used to be silently swallowed
    // (no Error subscriber at all). Force one deterministically via reflection against the
    // protected FileSystemWatcher.OnError — flooding a directory with enough real events to
    // overflow InternalBufferSize would be slow and racy, and isn't needed to prove the
    // handling contract: given an overflow, the session must (a) not crash, (b) say so
    // (loud, not swallowed), and (c) mark the activity as overflowed so a consumer knows any
    // changed-path list it might have built is incomplete, not empty.
    [Fact]
    public void ArmSourceWatch_WatcherBufferOverflow_IsHandledLoudly_NotSwallowed()
    {
        var dir = NewTempDir();
        var armed = AlRunner.WatchSource.ArmSourceWatch(new List<string> { dir });
        Assert.NotNull(armed);
        var (signal, watchers, activity) = armed!.Value;

        var savedErr = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            Assert.False(activity.Overflowed, "must not report overflow before one occurs.");

            var onError = typeof(FileSystemWatcher).GetMethod(
                "OnError", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(onError);
            var ex = new InternalBufferOverflowException("simulated overflow for #1904 test");
            onError!.Invoke(watchers[0], new object[] { new ErrorEventArgs(ex) });
        }
        finally
        {
            Console.SetError(savedErr);
        }

        Assert.True(activity.Overflowed,
            "a watcher Error event must mark the session as overflowed — not be swallowed.");
        Assert.True(signal.IsSet,
            "an overflow must force a cycle (set the signal) rather than leave the loop " +
            "asleep on a tree that may have changed underneath it.");
        Assert.Contains("buffer overflow", captured.ToString(), StringComparison.OrdinalIgnoreCase);

        foreach (var w in watchers) { w.EnableRaisingEvents = false; w.Dispose(); }
        signal.Dispose();
    }
}
