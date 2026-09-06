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
// The liveness test compares a BOOT-RELATIVE start time, not a wall-clock one. Process.StartTime
// on Linux is not anchored to the kernel's /proc/stat btime: each process derives its own base as
// (realtime now - /proc/uptime) the first time it asks, so two processes asking at different
// moments get bases that differ by the realtime-against-boottime skew accumulated in between.
// Measured on the reporting machine: .NET puts pid 1's StartTime at unix 1788245948.07 while
// btime + /proc/<pid>/stat field 22 gives 1788245947.13 — 0.95 s apart after 4.2 days of uptime,
// about 2.6 ppm. Under the old 2-second tolerance a --watch or --server session crossed it after
// roughly nine days and had its scratch directory deleted while still using it; a clock STEP
// (chrony makestep, a VM snapshot restore, suspend/resume) crossed it instantly and for EVERY
// sidecar on the machine at once, so one runner start could delete every live owner's directory,
// including a test host's in-use --cache root — a wrong test result rather than a visible failure.
// So the sidecar also records /proc/<pid>/stat field 22 (`startjiffies=`), which both the writer
// and the sweeper compute identically because it has no wall-clock anchor at all, and that value
// decides whenever it is present on both sides. The wall-clock comparison remains only as the
// fallback for a sidecar written without it (a pre-#2706 build, or a non-Linux host where
// StartTime is a fixed creation FILETIME and does not drift), with a 60 s tolerance so ordinary
// drift cannot reach it either.
//
// WHAT THIS CLASS DELIBERATELY DOES *NOT* COVER (#2967)
//
// The list above named what ScratchDirs owns and said nothing about the rest, so eight further
// runner-side temp sites sat outside it invisibly — two of them keyed on a NAME and then
// truncating or deleting what was there, the same shape as #2586. Recording the exclusions is
// the half that keeps the next remainder from being invisible too. Every site below has been
// classified; add to this table when you add a temp site, or explain why it is owned here.
//
//   SAFELY SHARED BY CONTENT ADDRESS — must NOT become per-process, sharing is the point:
//     al-runner-pkgdedup/<hash>/                 package-dedup staging (PkgDedupStaging)
//     al-runner-systemapp-<len>-<mtime>.app      extracted SystemApp package (RecordPatches)
//     alrunner-v2-win32-stubs/libwin32_stubs.so  the compiled Win32 shim (Win32Stubs)
//   Each publishes with one rename now, so a reader sees the name absent or complete, never
//   half-written; pkgdedup additionally validates a stage before reusing it, because its key
//   addresses PATHS and the files behind them can be deleted. None is owner-tracked: they have
//   no single owner by design and outliving their creator is what they are for.
//
//   PER-PROCESS, owner-marked here (converted from a name-only key by #2967):
//     al-runner-query-symbols/<name>-<hash>-<nonce>/   PerProcessScratch, from BcCompiler
//     al-runner-precompile/<name>-<hash>-<nonce>/      PerProcessScratch, from SiblingCompile
//     al-runner-sibling-symbols/<leaf>-<hash>-<nonce>/ SiblingSymbolsDirectory (#2586)
//
//   DOCUMENTED TRADE-OFFS that stay unowned, each with the reason at its call site:
//     bccompiler-dump/, gen_<name>.cs, <assembly>.dll   debug dumps behind an env flag; a
//                                                       predictable path is their only purpose
//                                                       and nothing ever reads them back
//     al-runner-startup.log                             append-only, one line per runner start,
//                                                       and it must survive a crashed process
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
    private static long? _ownStartJiffies;

    /// <summary>Tolerance for the FALLBACK wall-clock start-time comparison (see the file header).
    /// Only reached when no boot-relative value is available on both sides. Generous on purpose:
    /// a false "alive" costs one stale directory, a false "dead" costs a live run's data, and the
    /// pid-reuse case this guards against needs the pid space to wrap pid_max first.</summary>
    private const long WallClockStartToleranceTicks = 60 * TimeSpan.TicksPerSecond;

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
                $"pid={Environment.ProcessId}\nstart={OwnStartTicks}\nstartjiffies={OwnStartJiffies}\ncreated={DateTime.UtcNow:O}\n");
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

    /// <summary>
    /// Hand <paramref name="dir"/> to <paramref name="pid"/>: stop deleting it at this process's
    /// exit, and rewrite the sidecar so the sweep judges it by that pid's liveness instead of
    /// this one's (issue #2824).
    ///
    /// The case this exists for is the watchdog resume's carry directory. It is written by the
    /// PARENT attempt, which then sits waiting while the child runs — and the child does not read
    /// it until it writes its own outputs, at the very end. So the file must survive the child's
    /// whole run under an owner that is not using it, and killing the parent alone loses it two
    /// ways: SIGTERM runs the parent's ProcessExit, which deletes what it owns, and SIGKILL runs
    /// nothing but leaves an owner that is dead, so the next runner start sweeps it correctly.
    /// Naming the child instead makes both harmless.
    ///
    /// ORDER MATTERS. Disown first, then rewrite. Killed in between, this leaves a sidecar naming
    /// a dead parent, which the next sweep reclaims — exactly today's behaviour, so the window is
    /// no worse than not doing this at all. The other order has a window where the parent's own
    /// ProcessExit deletes a directory the sidecar has already promised to the child, which is
    /// worse than today.
    ///
    /// The recorded start time must be the one the SWEEP will compute for that pid, which on
    /// Linux is boot-relative (/proc/&lt;pid&gt;/stat field 22), not Process.StartTime — see this
    /// file's header for why the two disagree by accumulated clock skew. Writing the wall-clock
    /// value here would reintroduce precisely the drift #2706 removed, and it would do it to a
    /// directory whose whole point is to outlive its writer.
    ///
    /// Best-effort: a sidecar that cannot be rewritten leaves the directory owned as it was, which
    /// is the pre-#2824 behaviour and still correct, just more fragile.
    /// </summary>
    /// <returns>True when the sidecar now names <paramref name="pid"/>.</returns>
    public static bool TransferOwnership(string dir, int pid)
    {
        if (pid <= 0) return false;
        var full = Path.GetFullPath(dir);

        // Disown BEFORE rewriting — see the order note above.
        lock (Gate) Owned.Remove(full);

        long startTicks = 0;
        try { using var p = Process.GetProcessById(pid); startTicks = p.StartTime.ToUniversalTime().Ticks; }
        catch { /* already gone, or not inspectable: pid liveness alone will decide */ }
        var jiffies = TryReadStartJiffies(pid) ?? 0;

        try
        {
            File.WriteAllText(MarkerPathFor(full),
                $"pid={pid}\nstart={startTicks}\nstartjiffies={jiffies}\ncreated={DateTime.UtcNow:O}\n");
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Take responsibility for a directory a previous process handed to THIS one, so it is
    /// deleted at this process's exit rather than leaking (issue #2824).
    ///
    /// Adopts ONLY when the sidecar already names this process — which is true exactly when a
    /// parent called <see cref="TransferOwnership"/> for us, and false for a path a user typed on
    /// the command line. That condition is the whole safety argument: <c>--merge-counts</c> takes
    /// an arbitrary path, and a rule like "adopt the directory of every carry file" would have
    /// this process delete a directory of the caller's own at exit.
    /// </summary>
    /// <returns>True when this process now owns it.</returns>
    public static bool AdoptIfHandedToThisProcess(string dir)
    {
        var full = Path.GetFullPath(dir);
        if (!TryReadOwner(MarkerPathFor(full), out var pid, out _, out _)) return false;
        if (pid != Environment.ProcessId) return false;

        lock (Gate)
        {
            Owned.Add(full);
            if (!_exitHooked)
            {
                _exitHooked = true;
                AppDomain.CurrentDomain.ProcessExit += (_, _) => ReleaseAll();
            }
        }
        return true;
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

    /// <summary>This process's boot-relative start time, or 0 where it cannot be read (non-Linux).
    /// 0 in a sidecar means "not recorded" and sends the reader to the wall-clock fallback.</summary>
    private static long OwnStartJiffies
        => _ownStartJiffies ??= TryReadStartJiffies(Environment.ProcessId) ?? 0;

    /// <summary>
    /// The boot-relative start time of <paramref name="pid"/> in kernel clock ticks —
    /// <c>/proc/&lt;pid&gt;/stat</c> field 22 (<c>starttime</c>). Null when /proc is unavailable or
    /// the entry cannot be parsed. Unlike <see cref="Process.StartTime"/> this number is a property
    /// of the process rather than of the reader's clock, so the writer and a later sweeper always
    /// compute the same value for the same process.
    /// </summary>
    internal static long? TryReadStartJiffies(int pid)
    {
        if (!OperatingSystem.IsLinux() || pid <= 0) return null;
        string text;
        try { text = File.ReadAllText("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat"); }
        catch { return null; }

        // Field 2 (comm) is the executable name in parentheses and may itself contain spaces and
        // parentheses, so the only safe split point is the LAST ')'.
        var close = text.LastIndexOf(')');
        if (close < 0 || close + 1 >= text.Length) return null;
        var fields = text.Substring(close + 1).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // fields[0] is field 3 (state), so field 22 (starttime) is fields[19].
        if (fields.Length <= 19) return null;
        return long.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var t) && t > 0
            ? t : null;
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
        if (!TryReadOwner(markerPath, out var pid, out var startTicks, out var startJiffies))
            return;   // not ours to interpret; leave it and whatever it points at
        var target = markerPath.Substring(0, markerPath.Length - OwnerMarkerSuffix.Length);
        if (IsOwnerAlive(pid, startTicks, startJiffies)) { result.Kept++; return; }
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

    /// <summary>Parse a sidecar. Both start values are optional (0 = not recorded); with neither,
    /// pid liveness alone decides.</summary>
    internal static bool TryReadOwner(string markerPath, out int pid, out long startTicks)
        => TryReadOwner(markerPath, out pid, out startTicks, out _);

    /// <inheritdoc cref="TryReadOwner(string, out int, out long)"/>
    internal static bool TryReadOwner(string markerPath, out int pid, out long startTicks, out long startJiffies)
    {
        pid = 0; startTicks = 0; startJiffies = 0;
        string[] lines;
        try { lines = File.ReadAllLines(markerPath); } catch { return false; }
        var havePid = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("pid=", StringComparison.Ordinal))
                havePid = int.TryParse(line.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out pid);
            else if (line.StartsWith("startjiffies=", StringComparison.Ordinal))
                long.TryParse(line.AsSpan(13), NumberStyles.None, CultureInfo.InvariantCulture, out startJiffies);
            else if (line.StartsWith("start=", StringComparison.Ordinal))
                long.TryParse(line.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out startTicks);
        }
        return havePid && pid > 0;
    }

    /// <summary>
    /// Is the recorded owner still running? "Alive" needs the pid to exist AND, when a start
    /// time was recorded, the live process's start time to match it — a pid that has been
    /// reused by an unrelated process is a dead owner. Anything that cannot be determined
    /// (a process we are not allowed to inspect, a /proc entry that will not parse) counts as
    /// alive: the sweep's failure mode must be "left a stale directory behind", never "deleted
    /// a live one".
    ///
    /// <paramref name="startJiffies"/> — <c>/proc/&lt;pid&gt;/stat</c> field 22 — decides whenever
    /// it was recorded and can still be read, because it is the only one of the two the writer and
    /// this reader are guaranteed to compute identically. See the file header for why
    /// <see cref="Process.StartTime"/> alone is not safe to compare here.
    /// </summary>
    internal static bool IsOwnerAlive(int pid, long startTicks, long startJiffies = 0)
    {
        if (pid <= 0) return true;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;

            if (startJiffies > 0)
            {
                // Both sides read the same kernel field; a mismatch really is a different process.
                var liveJiffies = TryReadStartJiffies(pid);
                if (liveJiffies is not long lj) return true;   // cannot compare -> assume alive
                return lj == startJiffies;
            }

            if (startTicks <= 0) return true;
            long live;
            try { live = p.StartTime.ToUniversalTime().Ticks; }
            catch { return true; }
            return Math.Abs(live - startTicks) <= WallClockStartToleranceTicks;
        }
        catch (ArgumentException) { return false; }         // no process with that id
        catch (InvalidOperationException) { return false; } // exited between lookup and use
        catch { return true; }
    }

    private static bool ProcessExists(int pid) => IsOwnerAlive(pid, 0);
}
