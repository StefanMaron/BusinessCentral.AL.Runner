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
namespace AlRunner;

internal static class WatchSource
{
    /// <summary>
    /// Arm FileSystemWatchers over the bundles' source roots and return a settable
    /// signal + the watchers so the caller can interleave the file-change wait with
    /// other work (e.g. the interactive dashboard's keyboard polling). Returns null
    /// if there are no source dirs to watch — in that case the
    /// "[watch] no source directories to watch." diagnostic is printed and
    /// <paramref name="onArmed"/> is NOT invoked. The caller owns disposal of both
    /// the signal and the watchers.
    ///
    /// <paramref name="onArmed"/>, if given, runs after every watcher below has been
    /// constructed with EnableRaisingEvents = true — i.e. after the watch is
    /// genuinely live. Pass the "waiting for changes" print / dashboard paint here so
    /// it can never race the watcher (see file header — this is the #1822 fix).
    /// </summary>
    internal static (System.Threading.ManualResetEventSlim Signal, List<FileSystemWatcher> Watchers)? ArmSourceWatch(
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
        void OnChanged(object _, FileSystemEventArgs e)
        {
            if (e.FullPath.EndsWith(".al", StringComparison.OrdinalIgnoreCase)) signal.Set();
        }
        void OnRenamed(object _, RenamedEventArgs e)
        {
            if (e.FullPath.EndsWith(".al", StringComparison.OrdinalIgnoreCase)) signal.Set();
        }

        var watchers = new List<FileSystemWatcher>();
        foreach (var d in dirs)
        {
            var w = new FileSystemWatcher(d)
            {
                IncludeSubdirectories = true,
                Filter = "*.al",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            w.Changed += OnChanged; w.Created += OnChanged; w.Deleted += OnChanged; w.Renamed += OnRenamed;
            watchers.Add(w);
        }

        // Every watcher above was constructed with EnableRaisingEvents = true, so by the
        // time control reaches here the watch is genuinely live. onArmed runs ONLY now —
        // never before this point — which is the whole fix for #1822.
        onArmed?.Invoke();
        return (signal, watchers);
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
        var (signal, watchers) = armed.Value;
        try
        {
            signal.Wait();                       // block until the first .al change
            System.Threading.Thread.Sleep(250);  // debounce: let a save storm settle
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
