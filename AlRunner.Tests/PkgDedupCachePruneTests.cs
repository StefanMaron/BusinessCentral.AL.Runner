// PkgDedupCachePruneTests — the al-runner-pkgdedup staging root must be bounded, and
// bounding it must never take a stage directory away from a process still using it
// (issue #2990).
//
// The two halves are not equally important. "Something got deleted" is the easy half; the
// SURVIVAL cases below are the ones that decide whether this feature is a disk-hygiene fix
// or a data-loss bug, so most of this file is about directories that must still be there
// afterwards:
//
//   * a stage claimed by a LIVE process survives no matter how old it looks,
//   * a stage whose name is not one the runner writes is never touched at all,
//   * a stage used recently survives,
//   * a stage whose directory mtime is ancient but whose in-use marker is recent survives,
//   * ScratchDirs' own sweep still leaves a marked pkgdedup stage alone — the in-use marker
//     deliberately does NOT use the `.owner` suffix, because that sweep deletes the
//     directory when its owner dies, which for a deliberately shared cache would destroy
//     cross-run reuse on every process exit.
//
// Every case runs against a fixture root built by the test. Nothing here reads or writes
// the machine's real Path.GetTempPath()/al-runner-pkgdedup.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class PkgDedupCachePruneTests : IDisposable
{
    private readonly string _root;

    public PkgDedupCachePruneTests()
    {
        _root = TestScratch.Dir("al-runner-pkgdedup-prune-tests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    private static readonly DateTime Now = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A stage directory named exactly as BcCompiler names one, with one staged
    /// .app inside and a chosen last-use time.</summary>
    private string Stage(string name, TimeSpan age)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Contoso_App_1.0.0.0.app"), "NAVX");
        Directory.SetLastWriteTimeUtc(dir, Now - age);
        return dir;
    }

    private const string KeyA = "0123456789abcdef";
    private const string KeyB = "fedcba9876543210";

    // ── Removal ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Prune_RemovesStageUntouchedBeyondMaxAge()
    {
        var stale = Stage(KeyA, TimeSpan.FromDays(30));

        var result = PkgDedupCache.Prune(_root, MaxAge, Now);

        Assert.False(Directory.Exists(stale), "a stage untouched for 30 days must be reclaimed");
        Assert.Equal(new[] { stale }, result.Removed);
        Assert.Equal(0, result.Kept);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public void Prune_RemovesAbandonedTmpStagingDirectory()
    {
        // The `<key>.tmp-<rand>` scratch name. Normally renamed onto `<key>` within
        // milliseconds, but it survives the whole run when PkgDedupStaging.Publish falls
        // back to it, and forever when the process is killed mid-staging.
        var tmp = Stage(KeyA + ".tmp-0a1b2c3d", TimeSpan.FromDays(30));

        var result = PkgDedupCache.Prune(_root, MaxAge, Now);

        Assert.False(Directory.Exists(tmp), "an abandoned .tmp- staging dir must be reclaimed");
        Assert.Equal(new[] { tmp }, result.Removed);
    }

    [Fact]
    public void Prune_RemovesStageClaimedOnlyByADeadProcess()
    {
        var stale = Stage(KeyA, TimeSpan.FromDays(30));
        var marker = WriteMarker(KeyA, DeadPid, startJiffies: 0, age: TimeSpan.FromDays(30));

        var result = PkgDedupCache.Prune(_root, MaxAge, Now);

        Assert.False(Directory.Exists(stale), "a dead process's claim must not preserve a stage");
        Assert.False(File.Exists(marker), "the dead owner's marker must go with the directory");
        Assert.Contains(stale, result.Removed);
    }

    [Fact]
    public void Prune_RemovesOrphanMarkerLeftByADeadProcess()
    {
        // No directory: the stage was already gone (published elsewhere, or removed by an
        // earlier pass) and only the claim is left. Markers are files, so nothing else in
        // the tree would ever reclaim them.
        var marker = WriteMarker(KeyA, DeadPid, startJiffies: 0, age: TimeSpan.FromDays(30));

        var result = PkgDedupCache.Prune(_root, MaxAge, Now);

        Assert.False(File.Exists(marker), "an orphan marker whose owner is dead must be reclaimed");
        Assert.Equal(1, result.MarkersRemoved);
    }

    [Fact]
    public void Prune_RemovesLeftoverPruningDirectoryFromAnEarlierPass()
    {
        // Prune renames a doomed stage aside before deleting it, so no other process can
        // ever observe a half-emptied stage under its real name. A crash between the two
        // leaves the renamed tree behind; the next pass must finish the job.
        var leftover = Path.Combine(_root, KeyA + ".pruning-9f8e7d6c");
        Directory.CreateDirectory(leftover);
        File.WriteAllText(Path.Combine(leftover, "x.app"), "NAVX");

        var result = PkgDedupCache.Prune(_root, MaxAge, Now);

        Assert.False(Directory.Exists(leftover), "a leftover .pruning- tree must be finished off");
        Assert.Contains(leftover, result.Removed);
    }

    [Fact]
    public void Prune_RemovesOnlyTheStaleStage_LeavingItsNeighbourIntact()
    {
        var stale = Stage(KeyA, TimeSpan.FromDays(30));
        var fresh = Stage(KeyB, TimeSpan.FromHours(1));

        PkgDedupCache.Prune(_root, MaxAge, Now);

        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(fresh));
        Assert.True(File.Exists(Path.Combine(fresh, "Contoso_App_1.0.0.0.app")),
            "the surviving stage must keep its staged packages, not just its directory");
    }

    // ── Survival: the half that stops this becoming a data-loss bug ────────────────────

    [Fact]
    public void Prune_KeepsStageClaimedByALiveProcess_HoweverOldItLooks()
    {
        // THE load-bearing case. A --watch or --server session holds a stage for as long as
        // it runs and may not touch it again for days, so age alone cannot decide. This
        // process is the live claimant, and the directory is backdated far past any
        // threshold: only the liveness check can save it.
        var live = Stage(KeyA, TimeSpan.FromDays(3650));
        var marker = PkgDedupCache.MarkInUse(live);
        File.SetLastWriteTimeUtc(marker, Now - TimeSpan.FromDays(3650));
        Directory.SetLastWriteTimeUtc(live, Now - TimeSpan.FromDays(3650));

        var result = PkgDedupCache.Prune(_root, MaxAge, Now);

        Assert.True(Directory.Exists(live),
            "a stage claimed by a LIVE process must never be pruned, at any age");
        Assert.True(File.Exists(Path.Combine(live, "Contoso_App_1.0.0.0.app")),
            "and its staged packages must still be there");
        Assert.Empty(result.Removed);
        Assert.Equal(1, result.Kept);
    }

    [Fact]
    public void Prune_KeepsStageUsedWithinMaxAge()
    {
        var recent = Stage(KeyA, TimeSpan.FromDays(6));

        var result = PkgDedupCache.Prune(_root, MaxAge, Now);

        Assert.True(Directory.Exists(recent), "6 days is inside the 7-day threshold");
        Assert.Empty(result.Removed);
        Assert.Equal(1, result.Kept);
    }

    [Fact]
    public void Prune_KeepsStageWhoseMarkerIsRecentEvenWhenTheDirectoryMtimeIsAncient()
    {
        // Stamping the directory's mtime on reuse is best-effort — a read-only mount, a
        // different uid, a filesystem that refuses utimes. The marker write is the second,
        // independent record of "someone used this", so the newer of the two decides.
        Stage(KeyA, TimeSpan.FromDays(30));
        WriteMarker(KeyA, DeadPid, startJiffies: 0, age: TimeSpan.FromHours(2));

        var result = PkgDedupCache.Prune(_root, MaxAge, Now);

        Assert.True(Directory.Exists(Path.Combine(_root, KeyA)),
            "a claim recorded two hours ago is recent use, whatever the directory mtime says");
        Assert.Equal(1, result.Kept);
    }

    [Fact]
    public void Prune_KeepsOrphanMarkerOfALiveProcess()
    {
        // A live process claims the stage a moment before creating it. Deleting the claim
        // here would leave its directory unprotected for the rest of its run.
        var marker = PkgDedupCache.MarkInUse(Path.Combine(_root, KeyA));

        PkgDedupCache.Prune(_root, MaxAge, Now);

        Assert.True(File.Exists(marker), "a live process's claim must survive even with no directory yet");
    }

    [Theory]
    [InlineData("not-a-stage")]              // someone else's directory
    [InlineData("0123456789ABCDEF")]         // uppercase: never a name BcCompiler writes
    [InlineData("0123456789abcde")]          // 15 hex digits
    [InlineData("0123456789abcdef0")]        // 17 hex digits
    [InlineData("0123456789abcdef.tmp")]     // no random suffix
    [InlineData("0123456789abcdef.tmp-zz1b2c3d")] // non-hex random suffix
    [InlineData("0123456789abcdeg")]         // 'g' is not hex
    public void Prune_NeverTouchesADirectoryItDoesNotRecognise(string name)
    {
        // Fails closed: the prune deletes only names it can prove the runner wrote. Anything
        // else under the root belongs to someone else, and a shared temp root is exactly
        // where a wrong guess is unrecoverable.
        var foreign = Stage(name, TimeSpan.FromDays(3650));

        var result = PkgDedupCache.Prune(_root, MaxAge, Now);

        Assert.True(Directory.Exists(foreign), $"'{name}' is not a stage name the runner writes");
        Assert.Empty(result.Removed);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void Prune_OnAMissingRootDoesNothingAndDoesNotCreateIt()
    {
        var absent = Path.Combine(_root, "never-created");

        var result = PkgDedupCache.Prune(absent, MaxAge, Now);

        Assert.Empty(result.Removed);
        Assert.False(Directory.Exists(absent), "the prune must not conjure the root it is asked about");
    }

    // ── The marker itself ──────────────────────────────────────────────────────────────

    [Fact]
    public void MarkInUse_RecordsLastUseSoTheNextPruneSeesTheStageAsFresh()
    {
        // A stage is WRITTEN once and READ on every reuse, so its own mtime records only its
        // creation; atime is not usable (relatime/noatime are the defaults). MarkInUse is the
        // explicit last-USE stamp that makes the age rule mean what it says.
        var stage = Stage(KeyA, TimeSpan.FromDays(30));

        PkgDedupCache.MarkInUse(stage);

        var stamped = Directory.GetLastWriteTimeUtc(stage);
        Assert.True(DateTime.UtcNow - stamped < TimeSpan.FromMinutes(5),
            $"MarkInUse must stamp the stage's last-use time; it still reads {stamped:O}");
    }

    [Fact]
    public void MarkInUseMarker_IsNotAScratchDirsOwnerSidecar()
    {
        // Pinning the suffix choice, not just its spelling. ScratchDirs.SweepStale deletes a
        // directory whose `.owner` sidecar names a dead process. Every runner process exits,
        // so an `.owner` sidecar on a pkgdedup stage would destroy the shared cache on the
        // first sweep after the creating run — the cache exists precisely to be reused BY
        // LATER PROCESSES. An in-use claim therefore means "someone is reading this", never
        // "this belongs to me, delete it when I go".
        var stage = Stage(KeyA, TimeSpan.FromDays(30));
        var marker = PkgDedupCache.MarkInUse(stage);

        Assert.EndsWith(".inuse-" + Environment.ProcessId, marker);
        Assert.NotEqual(ScratchDirs.MarkerPathFor(stage), marker);
        Assert.False(File.Exists(ScratchDirs.MarkerPathFor(stage)));

        // And the sweep really does leave it alone, marker and all.
        ScratchDirs.SweepStale(_root);

        Assert.True(Directory.Exists(stage),
            "ScratchDirs' owner-liveness sweep must not reclaim a shared pkgdedup stage");
        Assert.True(File.Exists(marker));
    }

    // ── The configured threshold ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, 7)]      // unset: the default
    [InlineData("", 7)]
    [InlineData("14", 14)]
    [InlineData("0", 7)]       // non-positive would prune a stage in use right now
    [InlineData("-3", 7)]
    [InlineData("banana", 7)]
    [InlineData("99999999999", 7)]  // beyond TimeSpan.FromDays' range
    public void MaxAgeFromEnvironment_FallsBackToTheDefaultForAnythingUnusable(string? raw, int expectedDays)
    {
        Assert.Equal(TimeSpan.FromDays(expectedDays), PkgDedupCache.MaxAgeFrom(raw));
    }

    [Fact]
    public void MaxAgeFromEnvironment_HonoursAFractionalOverride()
    {
        Assert.Equal(TimeSpan.FromDays(0.5), PkgDedupCache.MaxAgeFrom("0.5"));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────

    /// <summary>A pid no process can have. Linux caps pid_max at 2^22 and Windows pids are far
    /// smaller still, so Process.GetProcessById cannot find one here and ScratchDirs.IsOwnerAlive
    /// reports it dead — portably, without spawning anything or relying on /proc.
    /// A non-positive pid would NOT work: ScratchDirs reads that as "unreadable, assume alive".</summary>
    private const int DeadPid = 2_146_999_999;

    private string WriteMarker(string stageName, int pid, long startJiffies, TimeSpan age)
    {
        var path = Path.Combine(_root, stageName + ".inuse-" + pid);
        File.WriteAllText(path, $"pid={pid}\nstart=1\nstartjiffies={startJiffies}\ncreated={Now:O}\n");
        File.SetLastWriteTimeUtc(path, Now - age);
        return path;
    }
}
