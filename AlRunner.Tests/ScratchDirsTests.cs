// ScratchDirsTests — the ownership rules behind the runner's temp-directory sweep (#2706).
//
// Every test here sweeps a PRIVATE root (the `tempRoot` parameter), never the real temp dir,
// so the suite can run beside live runners on the same machine without touching their
// scratch space. ScratchDirsRunnerStartupTests covers the real temp root end to end.
//
// The claims, both directions:
//   - a directory whose recorded owner is dead (no such pid, or pid reused by a process with a
//     different start time) is removed, sidecar included;
//   - a directory whose owner is alive is never touched;
//   - the two legacy pid-named shapes are swept by pid liveness;
//   - an unmarked directory — however old — and anything not named al-runner-*/alrunner-* is
//     never touched: the sweep does not guess by age;
//   - a live owner whose recorded WALL-CLOCK start time has drifted from the one this process
//     computes for it is still alive (the drift is real and unbounded — see the ScratchDirs
//     header), while a mismatched BOOT-RELATIVE start time is still a dead owner.

using System.Diagnostics;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class ScratchDirsTests : IDisposable
{
    private readonly string _root;

    public ScratchDirsTests()
    {
        // The private root is itself an owned scratch dir, so a killed test host leaves nothing.
        _root = TestScratch.Dir("al-runner-scratchdirs-unit");
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => ScratchDirs.Release(_root);

    private static void WriteMarker(string dir, int pid, long startTicks, long? startJiffies = null)
        => File.WriteAllText(ScratchDirs.MarkerPathFor(dir),
            $"pid={pid}\nstart={startTicks}\n" + (startJiffies is long j ? $"startjiffies={j}\n" : ""));

    private static string MakeDir(string path, string? payload = "payload")
    {
        Directory.CreateDirectory(path);
        if (payload != null) File.WriteAllText(Path.Combine(path, "f.bin"), payload);
        return path;
    }

    private static int DeadPid => ScratchDirsRunnerStartupTests.FindDeadPid();
    private static int LivePid => Environment.ProcessId;
    private static long LiveStartTicks
    {
        get { using var me = Process.GetCurrentProcess(); return me.StartTime.ToUniversalTime().Ticks; }
    }
    /// <summary>/proc/self/stat field 22. 0 off Linux, where it is not recorded.</summary>
    private static long LiveStartJiffies => ScratchDirs.TryReadStartJiffies(Environment.ProcessId) ?? 0;
    private const long TenSeconds = 10 * TimeSpan.TicksPerSecond;

    // ── Reserve / Create / Release ──────────────────────────────────────────────

    [Fact]
    public void Create_WritesSidecarNamingThisProcessAndItsStartTime()
    {
        var dir = ScratchDirs.Create(Path.Combine(_root, "al-runner-x", "a"));

        Assert.True(Directory.Exists(dir));
        var marker = ScratchDirs.MarkerPathFor(dir);
        Assert.True(File.Exists(marker), "sidecar missing: " + marker);
        Assert.True(ScratchDirs.TryReadOwner(marker, out var pid, out var start));
        Assert.Equal(Environment.ProcessId, pid);
        Assert.InRange(Math.Abs(start - LiveStartTicks), 0, 2 * TimeSpan.TicksPerSecond);
        Assert.True(ScratchDirs.IsOwnedByThisProcess(dir));
    }

    [Fact]
    public void Reserve_WritesSidecarButDoesNotCreateTheDirectory()
    {
        var dir = ScratchDirs.Reserve(Path.Combine(_root, "al-runner-y", "b"));

        Assert.False(Directory.Exists(dir), "Reserve must not create the leaf — callers rely on 'never written into' staying observable");
        Assert.True(File.Exists(ScratchDirs.MarkerPathFor(dir)));
    }

    [Fact]
    public void Release_DeletesDirectoryAndSidecarNow()
    {
        var dir = ScratchDirs.Create(Path.Combine(_root, "al-runner-z", "c"));
        File.WriteAllText(Path.Combine(dir, "f.bin"), "x");

        ScratchDirs.Release(dir);

        Assert.False(Directory.Exists(dir));
        Assert.False(File.Exists(ScratchDirs.MarkerPathFor(dir)));
        Assert.False(ScratchDirs.IsOwnedByThisProcess(dir));
    }

    // ── Sweep: sidecar-owned directories ────────────────────────────────────────

    [Fact]
    public void Sweep_KeepsDirectoryOwnedByLiveProcess()
    {
        var dir = ScratchDirs.Create(Path.Combine(_root, "al-runner-live", "d"));
        File.WriteAllText(Path.Combine(dir, "f.bin"), "x");

        var r = ScratchDirs.SweepStale(_root);

        Assert.True(File.Exists(Path.Combine(dir, "f.bin")), "live owner's directory was deleted");
        Assert.True(File.Exists(ScratchDirs.MarkerPathFor(dir)));
        Assert.Empty(r.Removed);
        Assert.Equal(1, r.Kept);
    }

    [Fact]
    public void Sweep_RemovesDirectoryWhoseOwnerNoLongerExists_NestedAndFlat()
    {
        var nested = MakeDir(Path.Combine(_root, "al-runner-dead", "e"));
        WriteMarker(nested, DeadPid, 1);
        var flat = MakeDir(Path.Combine(_root, "al-runner-dead-flat-e"));
        WriteMarker(flat, DeadPid, 1);

        var r = ScratchDirs.SweepStale(_root);

        Assert.False(Directory.Exists(nested));
        Assert.False(File.Exists(ScratchDirs.MarkerPathFor(nested)));
        Assert.False(Directory.Exists(flat));
        Assert.False(File.Exists(ScratchDirs.MarkerPathFor(flat)));
        Assert.Equal(2, r.Removed.Count);
        Assert.Equal(0, r.Failed);
    }

    [Fact]
    public void Sweep_TreatsReusedPidAsDeadOwner()
    {
        // Same pid as this (live) process, but a start time no process alive now can have.
        var dir = MakeDir(Path.Combine(_root, "al-runner-reused", "f"));
        WriteMarker(dir, LivePid, 1);

        var r = ScratchDirs.SweepStale(_root);

        Assert.False(Directory.Exists(dir), "pid reuse must not keep a dead owner's directory alive");
        Assert.Single(r.Removed);
    }

    [Fact]
    public void Sweep_KeepsLiveOwner_WhenItsRecordedWallClockStartTimeHasDrifted()
    {
        // Process.StartTime on Linux is derived per process from (realtime now - /proc/uptime), so
        // the value an owner records and the value a later sweeper computes for that same owner
        // drift apart with the owner's age, and jump together on any clock step. Ten seconds is
        // days of ordinary drift, or one chrony makestep. Both shapes must survive: with the
        // boot-relative value recorded (the fix), and without it (a sidecar from an older build).
        var bootRelative = MakeDir(Path.Combine(_root, "al-runner-drift", "boot-relative"));
        WriteMarker(bootRelative, LivePid, LiveStartTicks + TenSeconds, LiveStartJiffies);
        var wallClockOnly = MakeDir(Path.Combine(_root, "al-runner-drift", "wall-clock-only"));
        WriteMarker(wallClockOnly, LivePid, LiveStartTicks + TenSeconds);

        var r = ScratchDirs.SweepStale(_root);

        Assert.True(File.Exists(Path.Combine(bootRelative, "f.bin")),
            "a LIVE owner's directory was deleted because its recorded wall-clock start time had drifted");
        Assert.True(File.Exists(Path.Combine(wallClockOnly, "f.bin")),
            "a LIVE owner's directory with a pre-#2706 sidecar was deleted over wall-clock drift");
        Assert.Empty(r.Removed);
        Assert.Equal(2, r.Kept);
    }

    [Fact]
    public void Sweep_BootRelativeStartTime_IsAuthoritative_SoPidReuseIsStillDetected()
    {
        // /proc/<pid>/stat field 22 is a Linux fact; off Linux there is nothing to be authoritative.
        if (!OperatingSystem.IsLinux()) return;

        // The wall-clock start time matches exactly — under the widened tolerance alone this would
        // read as "same process". The boot-relative value says otherwise and wins, so the wider
        // tolerance is a fallback, not a hole in pid-reuse detection.
        var dir = MakeDir(Path.Combine(_root, "al-runner-jiffies", "reused"));
        WriteMarker(dir, LivePid, LiveStartTicks, LiveStartJiffies + 1);

        var r = ScratchDirs.SweepStale(_root);

        Assert.False(Directory.Exists(dir),
            "a mismatched boot-relative start time must be treated as a reused pid, i.e. a dead owner");
        Assert.Single(r.Removed);
    }

    [Fact]
    public void Create_RecordsTheBootRelativeStartTimeToo()
    {
        if (!OperatingSystem.IsLinux()) return;

        var dir = ScratchDirs.Create(Path.Combine(_root, "al-runner-jf", "a"));

        Assert.True(ScratchDirs.TryReadOwner(ScratchDirs.MarkerPathFor(dir), out var pid, out _, out var jiffies));
        Assert.Equal(Environment.ProcessId, pid);
        Assert.True(jiffies > 0, "sidecar carries no startjiffies= — the sweep would fall back to the drifting wall clock");
        Assert.Equal(LiveStartJiffies, jiffies);
    }

    [Fact]
    public void Sweep_MarkerWithoutStartTime_DecidesByPidLivenessAlone()
    {
        var dead = MakeDir(Path.Combine(_root, "al-runner-nostart", "dead"));
        File.WriteAllText(ScratchDirs.MarkerPathFor(dead), $"pid={DeadPid}\n");
        var live = MakeDir(Path.Combine(_root, "al-runner-nostart", "live"));
        File.WriteAllText(ScratchDirs.MarkerPathFor(live), $"pid={LivePid}\n");

        ScratchDirs.SweepStale(_root);

        Assert.False(Directory.Exists(dead));
        Assert.True(Directory.Exists(live));
    }

    [Fact]
    public void Sweep_RemovesOrphanSidecarOfDeadOwner_KeepsLiveOnes()
    {
        var deadTarget = Path.Combine(_root, "al-runner-orphan", "gone");
        Directory.CreateDirectory(Path.GetDirectoryName(deadTarget)!);
        WriteMarker(deadTarget, DeadPid, 1);
        var liveTarget = Path.Combine(_root, "al-runner-orphan", "reserved");
        WriteMarker(liveTarget, LivePid, LiveStartTicks);

        var r = ScratchDirs.SweepStale(_root);

        Assert.False(File.Exists(ScratchDirs.MarkerPathFor(deadTarget)));
        Assert.True(File.Exists(ScratchDirs.MarkerPathFor(liveTarget)), "a live Reserve()'d path's sidecar was removed");
        Assert.Single(r.Removed);
        Assert.Equal(1, r.Kept);
    }

    [Fact]
    public void Sweep_UnparseableSidecar_LeavesBothSidecarAndDirectoryAlone()
    {
        var dir = MakeDir(Path.Combine(_root, "al-runner-weird", "g"));
        File.WriteAllText(ScratchDirs.MarkerPathFor(dir), "not a marker\n");

        var r = ScratchDirs.SweepStale(_root);

        Assert.True(Directory.Exists(dir));
        Assert.True(File.Exists(ScratchDirs.MarkerPathFor(dir)));
        Assert.Empty(r.Removed);
    }

    // ── Sweep: legacy pid-named leaves ─────────────────────────────────────────

    [Fact]
    public void Sweep_LegacyPidNamedLeaves_RemovedWhenPidDead_KeptWhenAlive()
    {
        var dead = DeadPid;
        var depsDead = MakeDir(Path.Combine(_root, "al-runner-deps", "p" + dead));
        var depsLive = MakeDir(Path.Combine(_root, "al-runner-deps", "p" + LivePid));
        var navDead = MakeDir(Path.Combine(_root, "al-runner-navserver", dead.ToString()));
        var navLive = MakeDir(Path.Combine(_root, "al-runner-navserver", LivePid.ToString()));
        var userDead = MakeDir(Path.Combine(_root, "al-runner-navserver", "user", dead.ToString()));
        var userLive = MakeDir(Path.Combine(_root, "al-runner-navserver", "user", LivePid.ToString()));
        // Same digits, wrong container: not a legacy leaf, so not the sweep's to delete.
        var elsewhere = MakeDir(Path.Combine(_root, "al-runner-other", dead.ToString()));

        var r = ScratchDirs.SweepStale(_root);

        Assert.False(Directory.Exists(depsDead));
        Assert.False(Directory.Exists(navDead));
        Assert.False(Directory.Exists(userDead));
        Assert.True(Directory.Exists(depsLive), "live pid's al-runner-deps leaf was deleted");
        Assert.True(Directory.Exists(navLive), "live pid's al-runner-navserver leaf was deleted");
        Assert.True(Directory.Exists(userLive), "live pid's al-runner-navserver/user leaf was deleted");
        Assert.True(Directory.Exists(elsewhere), "a pid-looking name outside the legacy containers was deleted");
        Assert.Equal(3, r.Removed.Count);
        Assert.Equal(3, r.Kept);
    }

    // ── Sweep: what it must never touch ────────────────────────────────────────

    [Fact]
    public void Sweep_NeverDeletesUnmarkedDirectories_HoweverOld()
    {
        var old = MakeDir(Path.Combine(_root, "al-runner-legacy-fixture", Guid.NewGuid().ToString("N")));
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-90));
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(old)!, DateTime.UtcNow.AddDays(-90));
        var oldFlat = MakeDir(Path.Combine(_root, "al-runner-jobs-" + Guid.NewGuid().ToString("N")));
        Directory.SetLastWriteTimeUtc(oldFlat, DateTime.UtcNow.AddDays(-90));

        var r = ScratchDirs.SweepStale(_root);

        Assert.True(File.Exists(Path.Combine(old, "f.bin")));
        Assert.True(File.Exists(Path.Combine(oldFlat, "f.bin")));
        Assert.Empty(r.Removed);
    }

    [Fact]
    public void Sweep_IgnoresForeignNamesAndPlainFiles()
    {
        var foreign = MakeDir(Path.Combine(_root, "someone-elses-" + Guid.NewGuid().ToString("N")));
        WriteMarker(foreign, DeadPid, 1);   // even with a dead-owner sidecar: wrong prefix, not ours
        var file = Path.Combine(_root, "al-runner-systemapp-abc.app");
        File.WriteAllText(file, "x");
        var log = Path.Combine(_root, "al-runner-startup.log");
        File.WriteAllText(log, "x");

        var r = ScratchDirs.SweepStale(_root);

        Assert.True(Directory.Exists(foreign));
        Assert.True(File.Exists(ScratchDirs.MarkerPathFor(foreign)));
        Assert.True(File.Exists(file));
        Assert.True(File.Exists(log));
        Assert.Empty(r.Removed);
    }

    [Fact]
    public void Sweep_MissingRoot_IsNoOp()
    {
        var r = ScratchDirs.SweepStale(Path.Combine(_root, "does-not-exist"));
        Assert.Empty(r.Removed);
        Assert.Equal(0, r.Kept);
    }

    // ── Liveness primitive ─────────────────────────────────────────────────────

    [Fact]
    public void IsOwnerAlive_CurrentProcess_True_DeadPid_False_ReusedPid_False()
    {
        Assert.True(ScratchDirs.IsOwnerAlive(LivePid, LiveStartTicks));
        Assert.True(ScratchDirs.IsOwnerAlive(LivePid, 0));
        Assert.False(ScratchDirs.IsOwnerAlive(DeadPid, 0));
        Assert.False(ScratchDirs.IsOwnerAlive(LivePid, 1));
    }

    // ── The runner's own writers go through it ─────────────────────────────────

    [Fact]
    public void DepExtractionDir_Root_CarriesAnOwnerSidecar()
    {
        var root = DepExtractionDir.Root;
        Assert.True(Directory.Exists(root));
        Assert.True(File.Exists(ScratchDirs.MarkerPathFor(root)), "DepExtractionDir.Root has no .owner sidecar — a killed run's extraction dir could never be reclaimed");
        Assert.True(ScratchDirs.TryReadOwner(ScratchDirs.MarkerPathFor(root), out var pid, out _));
        Assert.Equal(Environment.ProcessId, pid);
    }
}
