// ScratchDirs — ownership-tracked scratch directories under the OS temp root (issue #2706).
//
// The runner writes several per-process / per-run scratch directories under
// Path.GetTempPath(): dependency source extraction (DepExtractionDir), the report engine's
// per-session temp folder (NavReportSync / MetadataPatches), --jobs shard reports
// (ParallelFanOut), --no-cache throwaway cache roots (CacheRoots), watchdog-resume carry
// files and --server inline-code bundles (Program.cs), the r2r-chunks write fallback
// (AppLoader). The test suite adds ~190 more sites of the same shape. Every one of them used
// to rely on the CREATING process deleting the directory at its own clean exit — or on
// nothing at all — so a run that was killed, OOM'd, or hit the watchdog left its directory
// behind forever. Measured on the reporting machine: 126 GB across the test fixtures'
// --cache roots, and al-runner-navserver/user/ alone regrew to 7.5 GB (212 per-pid folders,
// 211 of them for dead processes) within hours of being cleared. On a stock Linux desktop
// /tmp is a tmpfs, so that is RAM, charged to no process.
//
// A process cannot clean up after itself when it is killed, so the leftovers of a dead
// process can only be reclaimed by a LATER process. That needs an ownership record that
// outlives the owner, which is what this class adds:
//
//   * Reserve/Create write a sidecar file `<dir>.owner` beside the directory, naming the
//     owning pid AND its start time (a pid alone is reused; pid + start time is not).
//   * The owner deletes its directories and sidecars at ProcessExit (clean exit, SIGTERM,
//     Ctrl+C). Release does the same on demand.
//   * SweepStale — run once at every runner start — enumerates the temp root's al-runner-*
//     entries and deletes exactly those whose recorded owner is provably gone: no such pid,
//     or the pid now belongs to a process with a different start time. A live owner's
//     directory is never touched, so any number of runners (and test hosts) can share one
//     TMPDIR. Two legacy shapes that predate the sidecar are recognised by the pid in their
//     name (al-runner-deps/p<pid>, al-runner-navserver/[user/]<pid>) and swept by pid
//     liveness alone.
//
// Deliberately NO age-based rule for unmarked directories. An age heuristic is the only rule
// that can delete a directory a live process is still using — a --watch or --server session,
// or a test host from a build that predates this file — and this machine routinely runs
// several of those at once. Everything the runner itself writes is marked after #2706, so
// unmarked litter is either legacy (a one-time manual `rm`) or from an old build; neither is
// worth the risk of pulling a directory out from under a live run.
//
// The sweep is synchronous and cheap in steady state: one directory enumeration (plus one per
// al-runner-* container) when there is nothing stale. It only costs real time when there IS
// garbage — which is precisely the situation the issue is about — and the first sweep on a
// littered machine paying a few seconds once is the intended trade.

using System.Diagnostics;
using System.Globalization;

namespace AlRunner.Infrastructure;

public static class ScratchDirs
{
    /// <summary>Sidecar suffix: <c>&lt;dir&gt;.owner</c>, beside the directory, never inside it — so
    /// reserving a path does not have to create it, and a reader can decide a directory's fate
    /// without opening it.</summary>
    public const string OwnerMarkerSuffix = ".owner";

    /// <summary>Only names with one of these prefixes are ever examined by the sweep. Anything
    /// else in the temp root belongs to someone else.</summary>
    internal static readonly string[] ScannedPrefixes = { "al-runner-", "alrunner-" };

    /// <summary>How far below the temp root the sweep looks for sidecars and legacy pid-named
    /// leaves: <c>&lt;temp&gt;/X.owner</c> (0), <c>&lt;temp&gt;/al-runner-foo/X.owner</c> (1),
    /// <c>&lt;temp&gt;/al-runner-navserver/user/&lt;pid&gt;</c> (2).</summary>
    private const int MaxDepth = 2;

    /// <summary>Legacy per-process shapes written before the sidecar existed, keyed by the
    /// container path (relative to the temp root, '/'-joined) whose immediate children are named
    /// by pid with the given prefix.</summary>
    private static readonly (string Container, string LeafPrefix)[] LegacyPidLeaves =
    {
        ("al-runner-deps", "p"),
        ("al-runner-navserver", ""),
        ("al-runner-navserver/user", ""),
    };

    private static readonly object Gate = new();
    private static readonly HashSet<string> Owned = new(StringComparer.Ordinal);
    private static bool _exitHooked;
    private static long? _ownStartTicks;

    /// <summary>The sidecar path for <paramref name="dir"/>.</summary>
    public static string MarkerPathFor(string dir)
        => dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + OwnerMarkerSuffix;

    /// <summary>
    /// Claim <paramref name="dir"/> for this process WITHOUT creating it: writes the sidecar
    /// (creating the parent directory if needed) and schedules the directory and sidecar for
    /// deletion at ProcessExit. For callers that only hand the path on (e.g. a --no-cache root
    /// that the caches may or may not ever write into). Returns <paramref name="dir"/>.
    /// A sidecar that cannot be written is not fatal — the directory simply falls back to the
    /// pre-#2706 behaviour of relying on its owner's clean exit.
    /// </summary>
    public static string Reserve(string dir)
    {
        var full = Path.GetFullPath(dir);
        try
        {
            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            File.WriteAllText(MarkerPathFor(full),
                $"pid={Environment.ProcessId}\nstart={OwnStartTicks}\ncreated={DateTime.UtcNow:O}\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        lock (Gate)
        {
            Owned.Add(full);
            if (!_exitHooked)
            {
                _exitHooked = true;
                AppDomain.CurrentDomain.ProcessExit += (_, _) => ReleaseAll();
            }
        }
        return dir;
    }

    /// <summary><see cref="Reserve"/> plus <c>Directory.CreateDirectory</c>. Returns <paramref name="dir"/>.</summary>
    public static string Create(string dir)
    {
        Reserve(dir);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Delete <paramref name="dir"/> and its sidecar now, and forget it. Best-effort:
    /// cleanup failing must never fail the run it is cleaning up after.</summary>
    public static void Release(string dir)
    {
        var full = Path.GetFullPath(dir);
        lock (Gate) Owned.Remove(full);
        DeleteTreeAndMarker(full);
    }

    /// <summary>True when <paramref name="dir"/> was reserved by THIS process and not yet released.</summary>
    internal static bool IsOwnedByThisProcess(string dir)
    {
        var full = Path.GetFullPath(dir);
        lock (Gate) return Owned.Contains(full);
    }

    private static void ReleaseAll()
    {
        string[] snapshot;
        lock (Gate) { snapshot = Owned.ToArray(); Owned.Clear(); }
        foreach (var d in snapshot) DeleteTreeAndMarker(d);
    }

    private static bool DeleteTreeAndMarker(string full)
    {
        var ok = true;
        try { if (Directory.Exists(full)) Directory.Delete(full, recursive: true); }
        catch (IOException) { ok = false; }
        catch (UnauthorizedAccessException) { ok = false; }
        try { File.Delete(MarkerPathFor(full)); }
        catch (IOException) { ok = false; }
        catch (UnauthorizedAccessException) { ok = false; }
        return ok;
    }

    private static long OwnStartTicks
    {
        get
        {
            if (_ownStartTicks is long t) return t;
            long v;
            try { using var me = Process.GetCurrentProcess(); v = me.StartTime.ToUniversalTime().Ticks; }
            catch { v = 0; }
            _ownStartTicks = v;
            return v;
        }
    }

    // ── Sweep ─────────────────────────────────────────────────────────────────────────────

    /// <summary>What one <see cref="SweepStale"/> pass did. <see cref="Removed"/> lists the
    /// directories (or orphan sidecars) deleted; <see cref="Kept"/> counts entries examined and
    /// left alone because their owner is alive; <see cref="Failed"/> counts deletions that
    /// threw (a file in use, a permission problem) and will be retried by the next sweep.</summary>
    public sealed class SweepResult
    {
        public List<string> Removed { get; } = new();
        public int Kept { get; set; }
        public int Failed { get; set; }
    }

    /// <summary>
    /// Reclaim the scratch directories of processes that no longer exist. Examines only
    /// <see cref="ScannedPrefixes"/>-named entries under <paramref name="tempRoot"/> (default:
    /// <c>Path.GetTempPath()</c>), down to <see cref="MaxDepth"/>. Never throws — a sweep that
    /// cannot read some entry skips it. Safe to run from any number of processes concurrently:
    /// each decision is made per directory from that directory's own sidecar (or pid-named
    /// leaf), and only dead owners' directories are removed.
    /// </summary>
    public static SweepResult SweepStale(string? tempRoot = null)
    {
        var result = new SweepResult();
        string root;
        try { root = Path.GetFullPath(tempRoot ?? Path.GetTempPath()); }
        catch { return result; }
        if (!Directory.Exists(root)) return result;
        Scan(root, root, 0, result);
        return result;
    }

    private static void Scan(string root, string dir, int depth, SweepResult result)
    {
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir).ToList(); }
        catch { return; }

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (depth == 0 && !HasScannedPrefix(name)) continue;

            try
            {
                if (name.EndsWith(OwnerMarkerSuffix, StringComparison.Ordinal) && File.Exists(entry))
                {
                    HandleSidecar(entry, result);
                    continue;
                }
                if (!Directory.Exists(entry)) continue;   // a plain file: never the sweep's business
                if (File.Exists(MarkerPathFor(entry))) continue;   // decided by its sidecar, above or later in this loop

                if (depth >= 1 && TryLegacyPid(root, entry, name, out var legacyPid))
                {
                    if (ProcessExists(legacyPid)) { result.Kept++; }
                    else if (DeleteTreeAndMarker(entry)) result.Removed.Add(entry);
                    else result.Failed++;
                    continue;
                }

                if (depth < MaxDepth) Scan(root, entry, depth + 1, result);
            }
            catch { /* one unreadable entry must not stop the sweep */ }
        }
    }

    private static void HandleSidecar(string markerPath, SweepResult result)
    {
        if (!TryReadOwner(markerPath, out var pid, out var startTicks))
            return;   // not ours to interpret; leave it and whatever it points at
        var target = markerPath.Substring(0, markerPath.Length - OwnerMarkerSuffix.Length);
        if (IsOwnerAlive(pid, startTicks)) { result.Kept++; return; }
        if (DeleteTreeAndMarker(target)) result.Removed.Add(target);
        else result.Failed++;
    }

    private static bool HasScannedPrefix(string name)
    {
        foreach (var p in ScannedPrefixes)
            if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool TryLegacyPid(string root, string entry, string name, out int pid)
    {
        pid = 0;
        var parent = Path.GetDirectoryName(entry);
        if (parent == null) return false;
        var rel = Path.GetRelativePath(root, parent).Replace('\\', '/');
        foreach (var (container, leafPrefix) in LegacyPidLeaves)
        {
            if (!string.Equals(rel, container, StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.StartsWith(leafPrefix, StringComparison.Ordinal)) continue;
            var digits = name.Substring(leafPrefix.Length);
            return digits.Length > 0 && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out pid);
        }
        return false;
    }

    /// <summary>Parse a sidecar. <c>start</c> is optional (0 = unknown, pid liveness alone decides).</summary>
    internal static bool TryReadOwner(string markerPath, out int pid, out long startTicks)
    {
        pid = 0; startTicks = 0;
        string[] lines;
        try { lines = File.ReadAllLines(markerPath); } catch { return false; }
        var havePid = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("pid=", StringComparison.Ordinal))
                havePid = int.TryParse(line.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out pid);
            else if (line.StartsWith("start=", StringComparison.Ordinal))
                long.TryParse(line.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out startTicks);
        }
        return havePid && pid > 0;
    }

    /// <summary>
    /// Is the recorded owner still running? "Alive" needs the pid to exist AND, when a start
    /// time was recorded, the live process's start time to match it — a pid that has been
    /// reused by an unrelated process is a dead owner. Anything that cannot be determined
    /// (a process we are not allowed to inspect) counts as alive: the sweep's failure mode
    /// must be "left a stale directory behind", never "deleted a live one".
    /// </summary>
    internal static bool IsOwnerAlive(int pid, long startTicks)
    {
        if (pid <= 0) return true;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            if (startTicks <= 0) return true;
            long live;
            try { live = p.StartTime.ToUniversalTime().Ticks; }
            catch { return true; }
            return Math.Abs(live - startTicks) <= 2 * TimeSpan.TicksPerSecond;
        }
        catch (ArgumentException) { return false; }         // no process with that id
        catch (InvalidOperationException) { return false; } // exited between lookup and use
        catch { return true; }
    }

    private static bool ProcessExists(int pid) => IsOwnerAlive(pid, 0);
}
