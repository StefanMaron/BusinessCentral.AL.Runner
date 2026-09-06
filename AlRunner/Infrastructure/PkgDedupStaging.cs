// PkgDedupStaging — publishes BcCompiler's package-dedup staging directory from its
// per-run `<key>.tmp-<rand>` scratch name to the shared, content-addressed `<key>` name.
//
// Split out of BcCompiler.DeduplicateAppPackageDirs so the publish step can be unit
// tested on its own: BcCompiler drags the whole BC service-tier reference chain in with
// it, while this is plain filesystem logic.
//
// Why it is not a bare Directory.Move (#1691): on Windows the move of a directory whose
// files were written moments earlier intermittently fails with
//
//     IOException: Access to the path 'C:\...\Temp\al-runner-pkgdedup\<key>.tmp-<rand>' is denied.
//
// consistent with an AV scanner or the search indexer briefly holding a handle inside the
// freshly-written tree. It is transient — an immediate re-run of the same command always
// passes — but the old code rethrew, which surfaced as `EMIT-FAIL — IOException` and killed
// the whole run (reported at ~1 in 5 runs on a non-admin Windows box, where symlink creation
// is unavailable so every staged entry is a full file copy and the write volume is highest).
//
// Two changes make that non-fatal, in order of preference:
//   1. Retry the move a few times with a short backoff, so the common transient handle
//      clears and the shared, reusable `<key>` dir still gets published.
//   2. If it still will not move, fall back to using the scratch directory itself. Its
//      contents are exactly what the move would have published — same files, staged by the
//      same code path — so the compile that consumes it behaves identically. All that is
//      lost is cross-compile reuse of this one key, which is a cache miss, not a wrong
//      answer. That is strictly better than discarding a run that is already half done.
//
// SCRATCH-DIR CLASSIFICATION (#2967): al-runner-pkgdedup is SAFELY SHARED BY CONTENT ADDRESS
// and deliberately outlives its creator, so it is not a ScratchDirs-owned per-process
// directory and must not become one — deduplication across concurrent runners is the entire
// point of it. What was NOT safe is how a shared entry was REUSED, and that is what IsIntact
// below fixes.
//
// WHAT IS *NOT* WRONG WITH IT, MEASURED (#2967)
//   Three cross-process hazards were proposed for this site from a code read. Two do not
//   survive measurement, and saying so matters: the fix they imply is a cross-process
//   publication protocol, which is real complexity bought for a problem that is not there.
//
//   * "`lock (_stageSync)` in BcCompiler is in-process, so publication is unsynchronised
//     across the dozen runners on this box." True, and it does not matter. Publication needs
//     no lock because it is a single rename, and the lost-race case is handled explicitly
//     below by adopting the winner's directory.
//   * "`Directory.Exists(stage)` cannot tell a finished publish from one in progress, so a
//     reader compiles out of a partially populated stage." FALSE. `tmp` is a SIBLING of
//     `stage` (`stage + ".tmp-" + rand`), so both are on one filesystem and .NET's
//     `Directory.Move` is exactly one `rename(2)` — confirmed under strace, one
//     `rename("…/k2.tmp-f3c96bf6", "…/k2") = 0` per publish. `rename` is atomic, so the path
//     names either nothing or the fully populated directory; there is no third state to
//     observe. Measured on this machine, a reader process spinning on the same gate
//     BcCompiler uses, against a writer doing 1,500 publish cycles of 200 files each:
//     27,168 observations, ZERO partially populated. (A first run reported 91% partial and
//     was wrong — the harness tore the stage down with an in-place recursive delete, and was
//     measuring its own teardown. Renaming the directory away before deleting it removed
//     that, which is also why TryMoveAside below renames rather than deleting in place.)
//   * "`Publish` can return the scratch dir, so the result is not the content-addressed
//     path." True and harmless. That directory is GUID-named, private to this process, and
//     holds the identical staged set — a cache miss, never a wrong answer.
//
// WHY A REUSE CHECK IS NEEDED ANYWAY (#2967)
//   The key is a hash of the picked .app set's absolute PATHS, and each staged entry is a
//   symlink to one of those paths. A path is not the file: the target can be deleted (a test
//   fixture's temp tree is reclaimed at its owner's exit, a worktree is removed, a submodule
//   is deinitialized) long after the stage that points at it was published. The stage then
//   survives forever holding an entry that resolves to nothing, because the only gate on
//   reuse was `Directory.Exists(stage)` — which cannot see inside it — and nothing ever
//   prunes this root.
//
//   That is not hypothetical. Measured on the reporting machine on 2026-09-06, with nine
//   runners active: 138 stage directories, 28,004 staged entries, all of them symlinks, and
//   455 of those dangling across 73 of the 138 stages — 53% of the shared cache holding at
//   least one entry that does not resolve. Every dangling target's PARENT directory was gone
//   too, i.e. a whole source tree had been removed under a live stage.
//
//   Handing such a directory to BC's native package reader is a run-killing failure with
//   bundle-wide blast radius: it reports the missing package as
//   `AL1023 ... is not valid`, and DeduplicateAppPackageDirs' own comment records that the
//   error is attributed to the COMPILATION rather than to the package, so one unresolvable
//   entry fails every compile that scans the directory even when nothing references it.
//
//   So a stage is only adoptable when every entry in it still resolves. A stage that fails
//   that test is REPLACED rather than adopted — moved aside under a `.stale-<rand>` name
//   (one rename, atomic) so a concurrent reader is never left staring at a half-deleted
//   directory, then the freshly staged tmp is published in its place. Replacing rather than
//   merely bypassing matters: bypassing leaves the poison in place for the next runner, and
//   the next, forever.
//
// WHAT THIS DOES NOT FIX — AND WHERE THAT IS NOW FIXED (#2990)
//   Self-healing on reuse is the part that affects correctness, and it only reaches a stage
//   that is looked up again; a stage whose picked path set never recurs was never reclaimed at
//   all. That prune landed separately, in PkgDedupCache, and the hazard flagged here is the one
//   its design turns on: an age rule alone is the one rule that can delete a directory a live
//   process is reading, so a stage also has to be unclaimed by any live process before it goes.
//
//   One thing here feeds that prune. TryMoveAside's `.stale-<rand>` tree is removed by a
//   TryDelete that swallows every failure, so a tree it cannot remove stays under that name
//   with nothing else to reclaim it. PkgDedupCache recognises the name and finishes the job on
//   a later pass; it needs no liveness or age test, because the rename has already put the tree
//   beyond the reach of any lookup.
using System.Diagnostics;

namespace AlRunner.Infrastructure;

internal static class PkgDedupStaging
{
    internal const int DefaultAttempts = 5;

    /// <summary>
    /// Move <paramref name="tmp"/> onto <paramref name="stage"/> and return the directory
    /// callers should scan. That is <paramref name="stage"/> whenever it can be published or
    /// already exists, and <paramref name="tmp"/> when it cannot — never an exception, and
    /// never a directory that is missing the staged packages.
    /// </summary>
    /// <param name="warn">Where the fallback notice goes; null suppresses it.</param>
    /// <param name="attempts">Total move attempts, including the first.</param>
    /// <param name="backoff">
    /// Called with the 1-based attempt number after each failed attempt except the last.
    /// Tests inject it to drive the retry deterministically; production uses a short sleep.
    /// </param>
    internal static string Publish(string tmp, string stage, TextWriter? warn = null,
                                   int attempts = DefaultAttempts, Action<int>? backoff = null)
    {
        Debug.Assert(attempts >= 1);
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                Directory.Move(tmp, stage);
                return stage;
            }
            catch (Exception ex)
            {
                // Another compile — in this process or a concurrent al-runner — published the
                // same key first. The key is a hash of the picked .app set, so their directory
                // is ours by construction: adopt it and drop the duplicate.
                //
                // Only when it is INTACT, though. The key addresses paths, not bytes, so an
                // older stage under the same key can be holding entries whose targets have
                // since been deleted (see the file header for the measurement). Adopting one
                // of those would throw away the good tmp we just staged and hand the caller a
                // directory BC rejects with AL1023 — so replace it instead.
                if (Directory.Exists(stage))
                {
                    if (IsIntact(stage))
                    {
                        TryDelete(tmp);
                        return stage;
                    }
                    // Rename the poisoned stage out of the way rather than deleting it in
                    // place: a rename is one atomic step, so a concurrent reader either sees
                    // the old directory whole or sees it gone, never a directory being
                    // emptied file by file underneath it.
                    if (TryMoveAside(stage, warn)) continue;
                }
                if (attempt == attempts)
                {
                    warn?.WriteLine(
                        $"  [pkgdedup] could not publish staging dir to '{stage}' after " +
                        $"{attempts} attempt(s) ({ex.GetType().Name}: {ex.Message}) — " +
                        $"using '{tmp}' for this run");
                    return tmp;
                }
            }
            // Between attempts only. The caller holds BcCompiler's staging lock here, so this
            // is deliberately bounded and short (150ms total at the default 5 attempts) — the
            // handle this waits on is held by a scanner for milliseconds, and the alternative
            // being traded against is losing the run outright.
            if (backoff != null) backoff(attempt);
            else Thread.Sleep(10 * attempt);
        }
        return tmp; // not reachable: the attempts==attempt branch above always returns
    }

    /// <summary>
    /// True when <paramref name="stage"/> is a directory that can still be handed to BC's
    /// package reader: it exists, it is not empty, and every entry in it can be OPENED.
    /// <para>An empty stage counts as not intact. The staging branch only runs when there is
    /// at least one picked package, so an empty directory under a key is never a legitimate
    /// published result — it is an interrupted publish or an emptied leftover.</para>
    /// </summary>
    /// <remarks>
    /// It opens each entry rather than asking whether it exists, and that is not
    /// belt-and-braces. MEASURED on .NET 8 / Linux, against a symlink whose target had been
    /// deleted:
    /// <code>
    ///   File.Exists(dangling)                   = True
    ///   new FileInfo(dangling).Exists           = True
    ///   fi.ResolveLinkTarget(returnFinalTarget) = &lt;the missing path, non-null&gt;
    ///   File.OpenRead(dangling)                 = FileNotFoundException
    /// </code>
    /// Every existence-shaped API reports a broken link as present — the link itself is a
    /// directory entry that exists — so a check written with any of them would pass on
    /// exactly the directories this method exists to reject. Opening it is the same thing
    /// BC's native package reader does before it accepts a package, so it is also the
    /// question actually being asked. One open per entry, on directories that hold a few
    /// hundred at most, against a whole-run compile failure if it is wrong.
    /// </remarks>
    internal static bool IsIntact(string stage)
    {
        try
        {
            if (!Directory.Exists(stage)) return false;
            var any = false;
            foreach (var entry in Directory.EnumerateFileSystemEntries(stage))
            {
                any = true;
                try { using var probe = File.OpenRead(entry); }
                catch { return false; }
            }
            return any;
        }
        catch
        {
            // Unreadable is not usable. Treating it as intact would hand the caller a
            // directory we could not even enumerate.
            return false;
        }
    }

    /// <summary>
    /// Rename a poisoned stage to a unique sibling so the caller's freshly staged tmp can take
    /// its place, and delete the renamed copy. Returns false when it could not be moved — the
    /// caller then keeps retrying and ultimately falls back to its own scratch directory,
    /// which is always a correct answer.
    /// </summary>
    private static bool TryMoveAside(string stage, TextWriter? warn)
    {
        var aside = stage + ".stale-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Directory.Move(stage, aside);
        }
        catch
        {
            // Lost the race to another process doing the same repair, or the directory is
            // held. Either is fine: if it is now gone or intact the next attempt adopts or
            // publishes normally.
            return false;
        }
        warn?.WriteLine(
            $"  [pkgdedup] replaced stale staging dir '{stage}' — it held at least one entry " +
            "whose target no longer exists (BC reports those as AL1023)");
        TryDelete(aside);
        return true;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
