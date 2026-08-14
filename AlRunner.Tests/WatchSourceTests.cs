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
    // This test reproduces that shape deterministically WITHOUT a real BC compile: a burst
    // of 12 file writes with a 120ms gap between each (below the 250ms quiet window, so
    // each one re-arms it — exactly what a bulk git operation looks like from inotify's
    // side) spread over ~1.3s. Under the OLD fixed-sleep debounce, WaitForSourceChange
    // would return ~250ms after the FIRST write — i.e. around write #2 of 12, with 10 more
    // writes still to come. Under quiescence it must not return until `QuietMs` has passed
    // with NO further write, i.e. strictly after the LAST write of the burst.
    [Fact]
    public async Task WaitForSourceChange_BurstBelowQuietWindow_ReleasesOnlyAfterBurstSettles()
    {
        var dir = NewTempDir();
        const int fileCount = 12;
        const int gapMs = 120;
        Assert.True(gapMs < AlRunner.WatchSource.QuietMs,
            "the reproduction depends on each write re-arming quiescence — the gap must be " +
            "smaller than the quiet window.");

        var sw = new System.Diagnostics.Stopwatch();
        long lastWriteMs = -1;
        var burstDone = new System.Threading.ManualResetEventSlim(false);

        var task = Task.Run(() => AlRunner.WatchSource.WaitForSourceChange(
            new List<string> { dir },
            onArmed: () =>
            {
                sw.Start();
                _ = Task.Run(async () =>
                {
                    for (int i = 0; i < fileCount; i++)
                    {
                        File.WriteAllText(Path.Combine(dir, $"F{i}.Table.al"), $"table {60000 + i} F{i} {{ }}");
                        lastWriteMs = sw.ElapsedMilliseconds;
                        if (i < fileCount - 1) await Task.Delay(gapMs);
                    }
                    burstDone.Set();
                });
            }));

        var winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.True(ReferenceEquals(task, winner),
            "WaitForSourceChange did not return within 20s of the burst starting.");
        Assert.True(await task, "WaitForSourceChange must report a change was detected.");
        var returnedAtMs = sw.ElapsedMilliseconds;

        // The writer keeps running independently of WaitForSourceChange/the watchers (it
        // just writes files on its own timer) — wait for it to finish on its OWN terms
        // before reading lastWriteMs, rather than requiring it to already be done by the
        // time `task` returns. Requiring that here would conflate two different claims:
        // this wait proves the burst genuinely ran to completion (test-setup sanity); the
        // timing assertions below prove WHEN WaitForSourceChange released relative to it —
        // which, under the bug, is BEFORE the burst finishes. That is the defect, not a
        // reason to fail this wait.
        Assert.True(burstDone.Wait(TimeSpan.FromSeconds(5)),
            "the writer burst did not complete within 5s of WaitForSourceChange returning — " +
            "test setup is broken (unrelated to the #1904 timing claim below).");
        Assert.True(lastWriteMs >= 0, "the writer never wrote a file — the test setup is broken.");

        // The phantom-failure assertion: must not release before the burst's LAST write —
        // that is precisely "started a cycle mid-checkout". Fails under the old fixed
        // Thread.Sleep(250), which returns ~250ms after the FIRST write (~1070ms early here).
        Assert.True(returnedAtMs >= lastWriteMs,
            $"WaitForSourceChange returned at {returnedAtMs}ms, BEFORE the burst's last write " +
            $"at {lastWriteMs}ms — released mid-burst against a half-applied tree (the #1904 " +
            "phantom-failure bug: a fixed post-first-event debounce cannot distinguish a " +
            "settling save from an in-progress bulk rewrite).");

        // And it must not stall indefinitely either — a single quiet window plus generous
        // scheduling slack after the last write, not the 10s cap.
        Assert.True(returnedAtMs <= lastWriteMs + 3_000,
            $"WaitForSourceChange returned {returnedAtMs - lastWriteMs}ms after the burst's " +
            "last write — quiescence should release promptly once the burst settles, not " +
            "stall toward the cap.");
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
