// PkgDedupCache — bounding the shared package-dedup staging root (issue #2990).
//
// WHAT ACCUMULATES
//   BcCompiler.DeduplicateAppPackageDirs stages one symlink per deduplicated .app into
//   <temp>/al-runner-pkgdedup/<key>/, where <key> is a hash of the ABSOLUTE PATHS of the
//   picked .app set. That key space is unbounded in practice: fixture bundles live under
//   GUID-named temp trees, so every test run mints keys that will never be looked up again.
//   Nothing removed them. Measured on the reporting machine with nine runners active: 138
//   stage directories, 28,004 staged entries. On a stock Linux desktop /tmp is a tmpfs, so
//   that is RAM charged to no process — the same accumulation #2706 measured (126 GB) on the
//   test side, and the same one that has killed every shell on this machine at least once.
//
//   ScratchDirs.SweepStale already walks this root (it matches the `al-runner-` prefix) and
//   deliberately removes nothing here: it deletes only directories whose `.owner` sidecar
//   names a dead process, and a pkgdedup stage has no sidecar. That is not an oversight to
//   correct — see the next paragraph.
//
// WHY ScratchDirs' RULE CANNOT BE APPLIED AS-IS
//   ScratchDirs is single-owner: one process reserves a directory, and the directory dies
//   with that process. This cache is the opposite by design. Deduplicating across CONCURRENT
//   AND SUBSEQUENT runners is the entire point, so a stage must outlive the process that
//   created it. Giving a stage an `.owner` sidecar would have the next runner start delete it
//   as soon as its creator exited — which is every run — and the cache would never hit.
//   (PkgDedupCachePruneTests pins that, so the suffix cannot drift back onto `.owner`.)
//
// THE POLICY, AND WHY IT FAILS CLOSED
//   Removing a directory a live process is reading is worse than the disk it reclaims, so
//   three independent conditions must ALL hold before anything is deleted:
//
//     1. The name is one BcCompiler provably writes — `<16 hex>` or `<16 hex>.tmp-<8 hex>`.
//        Anything else under the root belongs to somebody else and is never touched, so a
//        future change to the naming scheme degrades to "stops reclaiming", never to
//        "deletes the wrong tree". This governs CLAIM FILES as well as directories: a file
//        spelt `<something>.inuse-<n>` is only read as a claim, and only ever deleted, when
//        `<something>` passes the same test (#3038). MarkInUse produces nothing else, so this
//        costs no reclamation; it keeps the code saying what the rule says.
//     2. No LIVE process claims it. Every process that stages or reuses a stage writes an
//        in-use marker beside it (`<stage>.inuse-<pid>`), in ScratchDirs' own sidecar format,
//        and liveness is decided by ScratchDirs.IsOwnerAlive — pid plus the boot-relative
//        start time, so a reused pid cannot masquerade as the original owner. Unlike an
//        `.owner` sidecar this is a claim by ANY NUMBER of processes at once, and it means
//        "someone is reading this", never "delete this when I exit".
//     3. It has not been used for `maxAge` — 7 days by default, overridable with
//        AL_RUNNER_PKGDEDUP_MAX_AGE_DAYS, which is floored at one hour so a mistyped
//        fraction cannot ask for condition-3-in-name-only. "Used" is the NEWER of the directory's own mtime
//        and its markers' mtimes, because a stage is written once and read on every reuse:
//        its own mtime otherwise records only its creation. atime cannot serve — relatime
//        and noatime are the norm — so MarkInUse stamps the directory explicitly, and the
//        marker mtime is the backup record for when that stamp cannot be written.
//
//   Condition 3 alone is the rule ScratchDirs' header warns about, the one thing that can
//   delete a directory a live process is still reading. Condition 2 is what makes it safe:
//   an idle --watch or --server session that has not touched its stage in a month still
//   holds a live claim on it. The threshold is generous anyway, because a run started from a
//   BUILD THAT PREDATES THIS FILE writes no marker at all, and condition 3 is the only thing
//   standing between such a run and its stage.
//
//   Deletion goes through a rename to `<name>.pruning-<rand>` first. The rename is atomic, so
//   no other process can ever observe a stage that exists under its real name but has been
//   half emptied — it sees the stage or it sees nothing, and "nothing" is a cache miss that
//   restages correctly. A crash between the rename and the delete leaves the aside tree,
//   which the next pass finishes.
//
//   The same finishing applies to `<name>.stale-<rand>`, which PkgDedupStaging.TryMoveAside
//   leaves when it replaces a stage whose entries no longer resolve (#2989). Its delete is
//   best-effort and swallows every failure, so a tree that could not be removed stays under
//   that name with nothing else in the root to reclaim it — the same unbounded growth this
//   class closes, reached through a sibling function rather than through the stage itself.
//
//   RESIDUAL RACE, stated rather than papered over: another process can pass
//   `Directory.Exists(stage)` in the instant between this pass reading the markers and
//   renaming the directory aside. It needs a stage nobody has used for seven days to be
//   picked up in exactly that window. The consequence is bounded: the packages vanish from
//   under the compile, BC's reader reports AL1023 and the run fails loudly with a compile
//   error. It cannot produce a passing test with the wrong answer, because a missing symbol
//   is a compile error, not a different result. Closing it completely needs a cross-process
//   lock around every stage read, which costs every compile to protect a seven-day-old edge.
using System.Globalization;
using System.Text;

namespace AlRunner.Infrastructure;

internal static class PkgDedupCache
{
    /// <summary>The shared staging root. Must stay in step with BcCompiler's `_stageRootCache`,
    /// which is the only thing that writes into it.</summary>
    internal static string Root => Path.Combine(Path.GetTempPath(), "al-runner-pkgdedup");

    /// <summary>Separates a stage name from the pid of a process reading it. Deliberately NOT
    /// <see cref="ScratchDirs.OwnerMarkerSuffix"/> — see this file's header.</summary>
    internal const string InUseInfix = ".inuse-";

    /// <summary>Marks a tree THIS class renamed aside for deletion. Recognised on a later pass so
    /// a prune interrupted between the rename and the delete is finished rather than leaked.</summary>
    internal const string PruningInfix = ".pruning-";

    /// <summary>What PkgDedupStaging.TryMoveAside renames a stale stage to before replacing it
    /// (#2989). Its delete is best-effort and swallows every failure, so a held or unreadable
    /// tree stays under this name forever with nothing else in the tree to reclaim it — the same
    /// leak this class exists to close, arrived at from a sibling function. Recognised here for
    /// the same reason and on the same terms as PruningInfix.</summary>
    internal const string StaleInfix = ".stale-";

    /// <summary>Far longer than any plausible run. The claim in condition 2 is what actually
    /// protects a live stage; this covers a claim that was never written (a pre-#2990 build)
    /// or lost (SIGKILL), so it buys safety at the price of a week of disk.</summary>
    internal static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(7);

    internal const string MaxAgeEnvVar = "AL_RUNNER_PKGDEDUP_MAX_AGE_DAYS";

    /// <summary>The shortest threshold <see cref="MaxAgeFrom"/> will hand back, whatever the
    /// environment asks for. Rejecting only zero and negatives was not enough: `0.00001` is a
    /// positive number of days and bought a 0.86-SECOND threshold (#3038), which is the very
    /// thing the fallback above exists to refuse, spelt differently.
    ///
    /// <para>Condition 3 is the only protection a directory carrying no claim has, and two
    /// such directories exist by construction: a `&lt;key&gt;.tmp-&lt;rand&gt;` that
    /// <c>PkgDedupStaging.Publish</c> has not returned from yet, and any stage a run from a
    /// build predating #2990 is reading. An hour is far longer than either staging or a
    /// compile and far shorter than the default, so it bounds a typo without taking the knob
    /// away.</para></summary>
    internal static readonly TimeSpan MinMaxAge = TimeSpan.FromHours(1);

    /// <summary>What one <see cref="Prune(string, TimeSpan, DateTime)"/> pass did.
    /// <see cref="Removed"/> lists deleted directories; <see cref="Kept"/> counts recognised
    /// stages examined and deliberately left; <see cref="Skipped"/> counts entries whose names
    /// the prune does not recognise and therefore refuses to judge; <see cref="Failed"/> counts
    /// deletions that threw and will be retried next pass.</summary>
    internal sealed class PruneResult
    {
        public List<string> Removed { get; } = new();
        public int Kept { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }
        public int MarkersRemoved { get; set; }
    }

    // ── In-use claims ──────────────────────────────────────────────────────────────────────

    private static readonly object Gate = new();
    private static readonly HashSet<string> Claimed = new(StringComparer.Ordinal);
    private static bool _exitHooked;
    private static long? _ownStartTicks;
    private static long? _ownStartJiffies;

    /// <summary>The claim path this process would write for <paramref name="stageDir"/>.</summary>
    internal static string InUseMarkerPath(string stageDir) => InUseMarkerPath(stageDir, Environment.ProcessId);

    internal static string InUseMarkerPath(string stageDir, int pid)
        => stageDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
           + InUseInfix + pid.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Record that this process is reading <paramref name="stageDir"/>, and stamp the stage's
    /// last-USE time. Call it on EVERY path that hands a stage to a compile — the one that
    /// created it and the one that reused an existing one — because a stage reused for months
    /// without ever being rewritten is exactly the one an age rule would otherwise delete.
    ///
    /// <para>The claim is removed at ProcessExit; the DIRECTORY is not. A shared cache entry
    /// must outlive the run that used it, which is the whole difference from
    /// <see cref="ScratchDirs.Reserve"/>.</para>
    ///
    /// <para>Best-effort throughout: a claim that cannot be written leaves the stage protected
    /// by the age rule alone, which is the pre-#2990 behaviour and still correct.</para>
    /// </summary>
    /// <returns>The claim path, whether or not it could be written.</returns>
    internal static string MarkInUse(string stageDir)
    {
        var full = Path.GetFullPath(stageDir);
        var marker = InUseMarkerPath(full);
        var now = DateTime.UtcNow;
        try
        {
            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            File.WriteAllText(marker,
                $"pid={Environment.ProcessId}\nstart={OwnStartTicks}\nstartjiffies={OwnStartJiffies}\n" +
                $"created={now:O}\n", Encoding.UTF8);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        // Separate try: the stamp failing must not lose the claim, and vice versa.
        try { if (Directory.Exists(full)) Directory.SetLastWriteTimeUtc(full, now); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        lock (Gate)
        {
            Claimed.Add(marker);
            if (!_exitHooked)
            {
                _exitHooked = true;
                AppDomain.CurrentDomain.ProcessExit += (_, _) => ReleaseAllClaims();
            }
        }
        return marker;
    }

    private static void ReleaseAllClaims()
    {
        string[] snapshot;
        lock (Gate) { snapshot = Claimed.ToArray(); Claimed.Clear(); }
        foreach (var m in snapshot)
        {
            // Only the claim. Never the directory — a later runner is meant to find it.
            try { File.Delete(m); } catch { }
        }
    }

    private static long OwnStartTicks
    {
        get
        {
            if (_ownStartTicks is long t) return t;
            long v;
            try { using var me = System.Diagnostics.Process.GetCurrentProcess(); v = me.StartTime.ToUniversalTime().Ticks; }
            catch { v = 0; }
            _ownStartTicks = v;
            return v;
        }
    }

    private static long OwnStartJiffies
        => _ownStartJiffies ??= ScratchDirs.TryReadStartJiffies(Environment.ProcessId) ?? 0;

    // ── Name recognition (condition 1) ─────────────────────────────────────────────────────

    /// <summary>
    /// True for the two names BcCompiler writes under the root: the published stage
    /// <c>&lt;16 lowercase hex&gt;</c> (SHA-256 of the picked set, truncated) and the scratch
    /// name it is published from, <c>&lt;16 hex&gt;.tmp-&lt;8 hex&gt;</c>.
    /// Everything else answers false and is never deleted.
    /// </summary>
    internal static bool IsStageDirName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var dot = name.IndexOf('.');
        var key = dot < 0 ? name : name[..dot];
        if (!IsLowerHex(key, 16)) return false;
        if (dot < 0) return true;
        var rest = name[dot..];
        return rest.StartsWith(".tmp-", StringComparison.Ordinal) && IsLowerHex(rest[5..], 8);
    }

    /// <summary>
    /// True for a stage tree that has already been renamed out from under its real name and
    /// whose deletion did not finish — either by this class (<see cref="PruningInfix"/>) or by
    /// PkgDedupStaging replacing a stale stage (<see cref="StaleInfix"/>).
    ///
    /// <para>These need no liveness or age test, and get none. The rename is what makes them
    /// unreachable: no lookup resolves a stage under one of these names, so no compile can adopt
    /// one, and the process that renamed it had already decided to delete it. Finishing that
    /// delete is strictly what the renamer intended.</para>
    /// </summary>
    internal static bool IsRenamedAsideLeftoverName(string name)
        => IsLeftoverWithInfix(name, PruningInfix) || IsLeftoverWithInfix(name, StaleInfix);

    private static bool IsLeftoverWithInfix(string name, string infix)
    {
        var idx = name.IndexOf(infix, StringComparison.Ordinal);
        if (idx <= 0) return false;
        return IsStageDirName(name[..idx]) && IsLowerHex(name[(idx + infix.Length)..], 8);
    }

    private static bool IsLowerHex(string s, int length)
    {
        if (s.Length != length) return false;
        foreach (var c in s)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        return true;
    }

    // ── The threshold (condition 3) ────────────────────────────────────────────────────────

    /// <summary>The configured threshold, or <see cref="DefaultMaxAge"/> when the environment
    /// says nothing usable. Anything non-positive or unparseable falls back rather than being
    /// honoured: "0" would mean "delete a stage the moment it is written", which is precisely
    /// the failure this class exists to avoid, and a typo must never be able to ask for it.
    /// A positive value below <see cref="MinMaxAge"/> is raised to it for the same reason —
    /// "0.00001" is not zero but asks for the same thing.</summary>
    internal static TimeSpan MaxAgeFromEnvironment()
        => MaxAgeFrom(Environment.GetEnvironmentVariable(MaxAgeEnvVar));

    internal static TimeSpan MaxAgeFrom(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DefaultMaxAge;
        if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var days))
            return DefaultMaxAge;
        if (!(days > 0) || double.IsNaN(days) || double.IsInfinity(days)) return DefaultMaxAge;
        TimeSpan asked;
        try { asked = TimeSpan.FromDays(days); }
        catch (OverflowException) { return DefaultMaxAge; }
        catch (ArgumentException) { return DefaultMaxAge; }
        return asked < MinMaxAge ? MinMaxAge : asked;
    }

    // ── The pass ───────────────────────────────────────────────────────────────────────────

    /// <summary>Prune the real root with the configured threshold. Never throws.</summary>
    internal static PruneResult Prune() => Prune(Root, MaxAgeFromEnvironment(), DateTime.UtcNow);

    /// <summary>
    /// One pass over <paramref name="root"/>. Deletes a stage only when all three conditions in
    /// this file's header hold. Never throws, and never creates <paramref name="root"/>: a
    /// machine that has never staged anything must not gain a directory by being swept.
    /// Safe to run concurrently from any number of processes — every decision is made per
    /// directory from that directory's own claims, and the aside-rename makes the removal
    /// atomic to anyone else looking.
    /// </summary>
    internal static PruneResult Prune(string root, TimeSpan maxAge, DateTime utcNow)
    {
        var result = new PruneResult();
        string[] entries;
        try
        {
            if (!Directory.Exists(root)) return result;
            entries = Directory.GetFileSystemEntries(root);
        }
        catch (IOException) { return result; }
        catch (UnauthorizedAccessException) { return result; }

        var markers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var stages = new List<string>();
        var leftovers = new List<string>();

        foreach (var entry in entries)
        {
            try
            {
                var name = Path.GetFileName(entry);
                var idx = name.LastIndexOf(InUseInfix, StringComparison.Ordinal);
                // Condition 1 applies here too (#3038). A claim is only ours to interpret —
                // and, further down, only ours to DELETE — when the thing it claims is a name
                // the runner provably writes. MarkInUse never targets anything else, so this
                // rejects nothing the runner produced; what it stops is judging a file that
                // merely happens to be spelt `<something>.inuse-<n>`. Rejected here rather
                // than in the orphan loop so the rule is applied once, and so such a file
                // lands in Skipped — "refused to judge" — instead of being silently swallowed
                // into the marker table and counted nowhere.
                if (idx > 0 && IsStageDirName(name[..idx]) && File.Exists(entry))
                {
                    var target = name[..idx];
                    if (!markers.TryGetValue(target, out var list)) markers[target] = list = new List<string>();
                    list.Add(entry);
                    continue;
                }
                if (!Directory.Exists(entry)) { result.Skipped++; continue; }
                if (IsRenamedAsideLeftoverName(name)) { leftovers.Add(entry); continue; }
                stages.Add(entry);
            }
            catch (IOException) { result.Skipped++; }
            catch (UnauthorizedAccessException) { result.Skipped++; }
        }

        // Trees renamed aside by an earlier prune, or by PkgDedupStaging replacing a stale
        // stage. Already unreachable under their real names, so nobody can adopt one: finish
        // the job the renamer started. See IsRenamedAsideLeftoverName.
        foreach (var leftover in leftovers)
        {
            if (TryDeleteTree(leftover)) result.Removed.Add(leftover);
            else result.Failed++;
        }

        foreach (var stage in stages)
        {
            var name = Path.GetFileName(stage);
            if (!IsStageDirName(name)) { result.Skipped++; continue; }   // condition 1
            markers.TryGetValue(name, out var claims);

            if (AnyClaimantAlive(claims)) { result.Kept++; continue; }   // condition 2
            if (utcNow - LastUseUtc(stage, claims) < maxAge) { result.Kept++; continue; }   // condition 3

            // Rename first so no other process ever sees a half-emptied stage under its real
            // name; then delete. A failure at either step is retried by the next pass.
            var aside = stage + PruningInfix + Guid.NewGuid().ToString("N")[..8];
            try { Directory.Move(stage, aside); }
            catch (IOException) { result.Failed++; continue; }
            catch (UnauthorizedAccessException) { result.Failed++; continue; }

            if (TryDeleteTree(aside)) result.Removed.Add(stage);
            else result.Failed++;
        }

        // Claims whose stage is gone — deleted just now, or by another runner. A claim is a
        // file, so nothing else in this tree would ever reclaim one. Only dead claimants'
        // claims go: a live process routinely claims a stage a moment before creating it.
        foreach (var (target, list) in markers)
        {
            if (Directory.Exists(Path.Combine(root, target))) continue;
            foreach (var marker in list)
            {
                if (IsClaimantAlive(marker)) continue;
                try { File.Delete(marker); result.MarkersRemoved++; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        return result;
    }

    /// <summary>The newer of the stage's own mtime and its claims' mtimes. Taking the maximum
    /// is what makes each record a backstop for the other: a reuse that could not stamp the
    /// directory still wrote a claim, and a claim lost to SIGKILL still left the stamp.</summary>
    private static DateTime LastUseUtc(string stage, List<string>? claims)
    {
        DateTime last;
        try { last = Directory.GetLastWriteTimeUtc(stage); }
        catch { return DateTime.UtcNow; }   // unreadable: treat as just-used, i.e. keep it
        if (claims == null) return last;
        foreach (var claim in claims)
        {
            try
            {
                var t = File.GetLastWriteTimeUtc(claim);
                if (t > last) last = t;
            }
            catch { /* vanished mid-pass: the remaining records still decide */ }
        }
        return last;
    }

    private static bool AnyClaimantAlive(List<string>? claims)
    {
        if (claims == null) return false;
        foreach (var claim in claims)
            if (IsClaimantAlive(claim)) return true;
        return false;
    }

    /// <summary>An unreadable or unparseable claim counts as ALIVE. It is the conservative
    /// answer, and the only cost of being wrong is one stage kept until the next pass.</summary>
    private static bool IsClaimantAlive(string markerPath)
    {
        if (!ScratchDirs.TryReadOwner(markerPath, out var pid, out var startTicks, out var startJiffies))
            return true;
        return ScratchDirs.IsOwnerAlive(pid, startTicks, startJiffies);
    }

    private static bool TryDeleteTree(string dir)
    {
        try { Directory.Delete(dir, recursive: true); return true; }
        catch (DirectoryNotFoundException) { return true; }   // someone else finished it
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
