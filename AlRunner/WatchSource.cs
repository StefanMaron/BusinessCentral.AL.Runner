// WatchSource — arms the `--watch` FileSystemWatchers.
//
// Moved out of Program.cs's top-level-statement local functions into its own internal
// static class for #1822: local functions declared inside top-level statements are
// nested inside the synthesized <Main>$ method and cannot be referenced from another
// file/class at all, so the arm-before-announce ordering contract had no way to be
// unit-tested. AlRunner.csproj already grants AlRunner.Tests InternalsVisibleTo, so an
// `internal` class here is directly testable — no new plumbing required.
//
// The bug this class exists to make impossible again: `--watch` used to print
// "[watch] waiting for AL source changes…" (or paint the interactive "● watching"
// dashboard) BEFORE arming the watchers. An edit landing in that window was lost —
// inotify has no backlog — so the process idled until killed. The fix is structural:
// `onArmed` is a caller-supplied callback that this class invokes itself, and ONLY
// after every FileSystemWatcher has EnableRaisingEvents = true. The caller can no
// longer announce "watching" before the watch is actually live, because the class
// controls when the announcement runs, not the caller.
//
// #1904: the debounce after the FIRST event used to be a fixed `Thread.Sleep(250)`.
// A single save fits comfortably inside that window, but a branch switch / rebase /
// bulk rename delivers dozens-to-thousands of file events over SECONDS, so the fixed
// wait released while the tree was still part-old / part-new — a cycle ran against a
// half-applied checkout and reported a phantom test result. The fix replaces the fixed
// sleep with quiescence: keep resetting the wait on every subsequent event and release
// only once `quietMs` has passed with NO further event, capped at `maxWaitMs` from the
// first event so a continuously-writing process cannot stall the loop forever. A single
// save still releases after one `quietMs` wait, same latency as before.
namespace AlRunner;

internal static class WatchSource
{
    /// <summary>
    /// How long the watch must see NO further `.al` event before a cycle is allowed to
    /// start — the quiescence window. Configurable via <c>AL_RUNNER_WATCH_QUIET_MS</c>
    /// (the right value depends on the machine and the size of the tree being switched);
    /// defaults to 250ms, the same latency a single save always paid before #1904.
    /// </summary>
    internal static int QuietMs { get; } = ReadPositiveIntEnv("AL_RUNNER_WATCH_QUIET_MS", 250);

    /// <summary>
    /// Hard cap, measured from the FIRST event of a burst, on how long quiescence will
    /// keep extending the wait. Without this a process that writes continuously (a huge
    /// checkout, a runaway build watcher) could keep the quiet window from ever being
    /// reached and starve the loop forever. Configurable via
    /// <c>AL_RUNNER_WATCH_MAX_WAIT_MS</c>; defaults to 10s, comfortably above the ~1.4s
    /// bulk-switch reproduction in #1904 but bounded so a stuck writer still yields a
    /// cycle eventually.
    /// </summary>
    internal static int MaxWaitMs { get; } = ReadPositiveIntEnv("AL_RUNNER_WATCH_MAX_WAIT_MS", 10_000);

    /// <summary>How often the quiescence loop polls for further activity.</summary>
    private const int PollIntervalMs = 25;

    private static int ReadPositiveIntEnv(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrEmpty(raw) && int.TryParse(raw, out var v) && v > 0 ? v : fallback;
    }

    /// <summary>
    /// Mutable activity tracker shared between a watch session's event handlers and
    /// whoever is waiting on it. Deliberately a class (not a struct/tuple) so the
    /// `OnChanged`/`OnRenamed`/`OnError` closures in <see cref="ArmSourceWatch"/> and the
    /// caller's wait loop observe the SAME instance — the whole point of quiescence is
    /// that a late event, arriving after the caller started waiting, is still seen.
    /// </summary>
    internal sealed class WatchActivity
    {
        // #1920: the clock is injectable so a test can drive this activity's notion of "now"
        // deterministically (a virtual clock, advanced only when the test's own fake `sleep`
        // callback runs) instead of depending on Environment.TickCount64 + real Task.Delay /
        // FileSystemWatcher timing, which is exactly what flaked on 27.3/28.3/28.4: a CI
        // runner under load can stretch a nominal 120ms gap between two writes past the
        // 250ms quiet window, and a real-time test has no way to tell that apart from a
        // genuine quiescence-logic regression. Production code never passes this — it always
        // defaults to Environment.TickCount64, so real behavior is unchanged.
        private readonly Func<long> _now;
        private long _lastEventTicks;
        private int _overflowed;

        internal WatchActivity(Func<long>? nowTicks = null)
        {
            _now = nowTicks ?? (() => Environment.TickCount64);
            _lastEventTicks = _now();
        }

        /// <summary>Clock ticks (see <see cref="_now"/>) of the most recent watcher event.</summary>
        internal long LastEventTicks => System.Threading.Volatile.Read(ref _lastEventTicks);

        /// <summary>
        /// True once any watcher's internal buffer has overflowed (<see
        /// cref="FileSystemWatcher.Error"/>) during this session — see <see
        /// cref="ArmSourceWatch"/>'s <c>OnError</c> handler. A buffer overflow means the
        /// watcher dropped events, so any "which paths changed" list built from the event
        /// stream is a strict subset of what actually changed and must not be trusted as
        /// complete.
        /// </summary>
        internal bool Overflowed => System.Threading.Volatile.Read(ref _overflowed) != 0;

        internal void Touch() => System.Threading.Volatile.Write(ref _lastEventTicks, _now());

        internal void MarkOverflow()
        {
            System.Threading.Interlocked.Exchange(ref _overflowed, 1);
            Touch(); // an overflow is itself activity — it must not let quiescence release early
        }
    }

    /// <summary>
    /// Arm FileSystemWatchers over the bundles' source roots and return a settable
    /// signal + the watchers + an activity tracker, so the caller can interleave the
    /// file-change wait with other work (e.g. the interactive dashboard's keyboard
    /// polling). Returns null if there are no source dirs to watch — in that case the
    /// "[watch] no source directories to watch." diagnostic is printed and
    /// <paramref name="onArmed"/> is NOT invoked. The caller owns disposal of both
    /// the signal and the watchers.
    ///
    /// <paramref name="onArmed"/>, if given, runs after every watcher below has been
    /// constructed with EnableRaisingEvents = true — i.e. after the watch is
    /// genuinely live. Pass the "waiting for changes" print / dashboard paint here so
    /// it can never race the watcher (see file header — this is the #1822 fix).
    /// </summary>
    internal static (System.Threading.ManualResetEventSlim Signal, List<FileSystemWatcher> Watchers, WatchActivity Activity)? ArmSourceWatch(
        List<string> bundles, Action? onArmed = null)
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in bundles)
        {
            var abs = Path.GetFullPath(b);
            var root = FindBucketRoot(abs) ?? abs;
            if (Directory.Exists(root)) dirs.Add(root);
        }
        if (dirs.Count == 0)
        {
            Console.Error.WriteLine("[watch] no source directories to watch.");
            return null;
        }

        var signal = new System.Threading.ManualResetEventSlim(false);
        var activity = new WatchActivity();
        void OnChanged(object _, FileSystemEventArgs e)
        {
            if (!e.FullPath.EndsWith(".al", StringComparison.OrdinalIgnoreCase)) return;
            activity.Touch();
            signal.Set();
        }
        void OnRenamed(object _, RenamedEventArgs e)
        {
            if (!e.FullPath.EndsWith(".al", StringComparison.OrdinalIgnoreCase)) return;
            activity.Touch();
            signal.Set();
        }
        // #1904 second exposure: the Error event used to be unhandled, so a watcher-buffer
        // overflow (InternalBufferSize's 8KB default fills fast during a bulk rewrite) was
        // silently swallowed — the watcher keeps running but drops events, which can leave
        // the loop asleep on a tree that changed (reads to a developer as "watch stopped
        // working"). Handle it: log it so it's visible, mark the session as overflowed, and
        // treat it as activity so quiescence does not release on a partial event picture.
        void OnError(object _, ErrorEventArgs e)
        {
            activity.MarkOverflow();
            signal.Set();
            Console.Error.WriteLine(
                "[watch] warning: file watcher buffer overflow ("
                + e.GetException().Message + ") — some change events may have been "
                + "dropped; forcing a re-scan.");
        }

        var watchers = new List<FileSystemWatcher>();
        foreach (var d in dirs)
        {
            var w = new FileSystemWatcher(d)
            {
                IncludeSubdirectories = true,
                Filter = "*.al",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                // 64KB is the practical ceiling FileSystemWatcher accepts without editing
                // the OS's inotify/registry limits (values above it are silently clamped on
                // Windows; Linux's inotify backing has no such cap but there is no harm in
                // asking for more here). 8x the 8KB default buys real headroom against the
                // burst a branch switch / bulk rewrite produces before OnError even matters.
                InternalBufferSize = 65536,
                EnableRaisingEvents = true,
            };
            w.Changed += OnChanged; w.Created += OnChanged; w.Deleted += OnChanged; w.Renamed += OnRenamed;
            w.Error += OnError;
            watchers.Add(w);
        }

        // Every watcher above was constructed with EnableRaisingEvents = true, so by the
        // time control reaches here the watch is genuinely live. onArmed runs ONLY now —
        // never before this point — which is the whole fix for #1822.
        onArmed?.Invoke();
        return (signal, watchers, activity);
    }

    /// <summary>
    /// Block until <paramref name="activity"/> has been quiet (no touch) for <see
    /// cref="QuietMs"/>, capped at <see cref="MaxWaitMs"/> measured from the moment this
    /// method is entered (i.e. from the first event that woke the caller — the caller is
    /// expected to call this immediately after its own <c>signal.Wait()</c>/`IsSet` check
    /// returns, so "entered" and "first event" are the same instant to within a poll
    /// interval). This is the #1904 fix: releasing on quiescence rather than a fixed delay
    /// after only the first event means a burst of events (a branch switch, a bulk
    /// rewrite) keeps re-arming the wait until the tree actually stops changing, instead of
    /// letting a cycle start against a half-applied checkout.
    ///
    /// <paramref name="nowTicks"/> and <paramref name="sleep"/> are injectable (#1920) so a
    /// test can prove this algorithm's quiescence behavior with a virtual clock instead of
    /// real <see cref="System.Threading.Thread.Sleep(int)"/> + <see
    /// cref="Environment.TickCount64"/>, which is real wall-clock time and therefore at the
    /// mercy of CI machine load — see <c>WatchSourceTests.WaitForQuiescence_*</c>. Production
    /// code never passes either — both default to the real clock/sleep, so behavior here is
    /// unchanged from before #1920.
    /// </summary>
    internal static void WaitForQuiescence(
        WatchActivity activity, int? quietMs = null, int? maxWaitMs = null,
        Func<long>? nowTicks = null, Action<int>? sleep = null)
    {
        var quiet = quietMs ?? QuietMs;
        var maxWait = maxWaitMs ?? MaxWaitMs;
        var now = nowTicks ?? (() => Environment.TickCount64);
        var doSleep = sleep ?? (ms => System.Threading.Thread.Sleep(ms));
        var deadline = now() + maxWait;
        while (true)
        {
            var current = now();
            var sinceLastEvent = current - activity.LastEventTicks;
            if (sinceLastEvent >= quiet) return;      // settled: no event for a full quiet window
            if (current >= deadline) return;          // cap hit: force a cycle regardless
            var sleepFor = Math.Min(quiet - sinceLastEvent, PollIntervalMs);
            doSleep((int)Math.Max(1, sleepFor));
        }
    }

    /// <summary>
    /// Block until an .al file changes under any watched bundle's bucket root. Returns
    /// true on a change (loop again), false if there is nothing to watch. <paramref
    /// name="onArmed"/> runs after arming succeeds and before the blocking wait — pass
    /// the "waiting for changes" announcement here so it can never race the watcher.
    /// </summary>
    internal static bool WaitForSourceChange(List<string> bundles, Action onArmed)
    {
        var armed = ArmSourceWatch(bundles, onArmed);
        if (armed == null) return false;
        var (signal, watchers, activity) = armed.Value;
        try
        {
            signal.Wait();                  // block until the first .al change
            WaitForQuiescence(activity);     // #1904: quiescence, not a fixed post-first-event sleep
            return true;
        }
        finally
        {
            foreach (var w in watchers) { w.EnableRaisingEvents = false; w.Dispose(); }
            signal.Dispose();
        }
    }

    // The bucket-root walk-up (climb parent directories until an app.json is found).
    // #1824: this is now the SINGLE shared implementation — Program.cs's own copy of
    // this exact loop (8 live call sites there) has been replaced with a delegating
    // call to this method, rather than the two staying in sync by hand. `internal` (not
    // `private`) so Program.cs's top-level-statement code — which lives in this same
    // assembly and namespace but, being a local function nested inside the synthesized
    // <Main>$ method, cannot itself be called INTO from elsewhere — can call OUT to this
    // one. AlRunner.Tests also reaches it directly (AlRunner.csproj's
    // InternalsVisibleTo) — see FindBucketRootDedupeTests.cs for the walk-up's own
    // pinned behavior.
    internal static string? FindBucketRoot(string bundlePath)
    {
        var cur = Directory.Exists(bundlePath) ? bundlePath : Path.GetDirectoryName(bundlePath);
        while (!string.IsNullOrEmpty(cur))
        {
            if (File.Exists(Path.Combine(cur, "app.json"))) return cur;
            var parent = Path.GetDirectoryName(cur);
            if (parent == cur) return null;
            cur = parent;
        }
        return null;
    }
}
