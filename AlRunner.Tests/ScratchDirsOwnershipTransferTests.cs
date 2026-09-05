// ScratchDirsOwnershipTransferTests — handing a scratch directory to the process that needs it
// (issue #2824).
//
// The watchdog resume's carry directory is written by the PARENT attempt and read by the CHILD,
// at the very end of a run that may take minutes. Owned by the parent it dies with the parent:
// SIGTERM runs the parent's ProcessExit, which deletes what it owns, and SIGKILL leaves an owner
// that is dead, so the next runner start sweeps it correctly and legitimately. #2747/#2822 made
// that loss LOUD; this makes it rare, and the two are independent — nothing here removes the need
// to shout when a carry file is missing for some other reason.
//
// As in #2747, these test the BEHAVIOUR rather than the scenario: what matters is what the sweep
// and the exit handler do with a transferred directory, and that is decidable without racing a
// kill against a real resume.
//
// The subtle half is the recorded start time. The sweep's liveness rule is boot-relative on Linux
// (/proc/<pid>/stat field 22), because two processes disagree about Process.StartTime by
// accumulated clock skew (#2706). A transfer that wrote the WRITER's boot-relative value, or a
// wall-clock one, would hand the child a sidecar the sweeper reads as a dead owner — reintroducing
// the drift on the one directory whose whole purpose is to outlive its writer.

using System.Diagnostics;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class ScratchDirsOwnershipTransferTests : IDisposable
{
    private readonly string _root = TestScratch.Dir("al-runner-owner-transfer-tests");
    private readonly List<Process> _spawned = new();

    public ScratchDirsOwnershipTransferTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var p in _spawned) { try { if (!p.HasExited) p.Kill(true); } catch { } p.Dispose(); }
        ScratchDirs.Release(_root);
    }

    /// <summary>A real, live process to hand a directory to — the stand-in for the resumed child.</summary>
    private Process LiveProcess()
    {
        var p = Process.Start(new ProcessStartInfo("sleep", "120") { UseShellExecute = false })!;
        _spawned.Add(p);
        return p;
    }

    private string MakeDir(string name)
    {
        var dir = Path.Combine(_root, "al-runner-resume-" + name);
        ScratchDirs.Create(dir);
        File.WriteAllText(Path.Combine(dir, "attempt.xml"), "<testsuites/>");
        return dir;
    }

    [Fact]
    public void Transfer_StopsThisProcessOwningIt()
    {
        // Half the fix: the parent's own ProcessExit must no longer delete a directory it has
        // promised to the child. ReleaseAll works off exactly this set.
        var dir = MakeDir("disown");
        var child = LiveProcess();
        Assert.True(ScratchDirs.IsOwnedByThisProcess(dir), "precondition: we own it before the handoff");

        Assert.True(ScratchDirs.TransferOwnership(dir, child.Id));

        Assert.False(ScratchDirs.IsOwnedByThisProcess(dir),
            "after the handoff this process must not delete the directory at its own exit");
    }

    [Fact]
    public void Transfer_NamesTheNewOwnerWithITSBootRelativeStartTime()
    {
        // The correctness point. The sidecar must carry the value the SWEEPER will compute for
        // that pid, not one computed for us — otherwise the sweep reads a live owner as dead and
        // deletes the directory during the child's run, which is the very failure being fixed.
        if (!OperatingSystem.IsLinux()) return;   // /proc/<pid>/stat field 22 is a Linux fact

        var dir = MakeDir("jiffies");
        var child = LiveProcess();

        ScratchDirs.TransferOwnership(dir, child.Id);

        Assert.True(ScratchDirs.TryReadOwner(ScratchDirs.MarkerPathFor(dir),
            out var pid, out _, out var jiffies));
        Assert.Equal(child.Id, pid);
        var childJiffies = ScratchDirs.TryReadStartJiffies(child.Id);
        Assert.NotNull(childJiffies);
        Assert.Equal(childJiffies!.Value, jiffies);
        // And it is genuinely the CHILD's, not ours copied across.
        Assert.NotEqual(ScratchDirs.TryReadStartJiffies(Environment.ProcessId), jiffies);
    }

    [Fact]
    public void AfterTransfer_TheSweepLeavesItAloneWhileTheNewOwnerLives()
    {
        // The other half: a sweep by any runner on the machine must read the directory as live.
        var dir = MakeDir("survives");
        var child = LiveProcess();

        ScratchDirs.TransferOwnership(dir, child.Id);
        var r = ScratchDirs.SweepStale(_root);

        Assert.True(Directory.Exists(dir), "a directory handed to a LIVE process was swept away");
        Assert.Empty(r.Removed);
    }

    [Fact]
    public void AfterTheNewOwnerDies_TheSweepReclaimsIt()
    {
        // The handoff must not create an immortal directory: once the child is gone nobody needs
        // the carry files, and #2706's guarantee that nothing leaks still has to hold.
        var dir = MakeDir("reclaimed");
        var child = LiveProcess();
        ScratchDirs.TransferOwnership(dir, child.Id);
        child.Kill(true);
        child.WaitForExit();

        var r = ScratchDirs.SweepStale(_root);

        Assert.False(Directory.Exists(dir), "a directory whose new owner has died must be reclaimed");
        Assert.Single(r.Removed);
    }

    [Fact]
    public void Transfer_ToADeadPid_IsReclaimedImmediately()
    {
        var dir = MakeDir("deadpid");
        ScratchDirs.TransferOwnership(dir, ScratchDirsRunnerStartupTests.FindDeadPid());

        ScratchDirs.SweepStale(_root);

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Transfer_RefusesANonsensePid_AndKeepsOwnership()
    {
        // Failing toward "we still own it" is the safe direction: the directory is then deleted at
        // our exit exactly as before, which is the pre-#2824 behaviour, not a leak.
        var dir = MakeDir("nonsense");

        Assert.False(ScratchDirs.TransferOwnership(dir, 0));

        Assert.True(ScratchDirs.IsOwnedByThisProcess(dir));
    }

    [Fact]
    public void Adopt_TakesADirectoryHandedToThisProcess()
    {
        var dir = MakeDir("adopt");
        // Simulate a parent handing it to us: the sidecar names OUR pid.
        ScratchDirs.TransferOwnership(dir, Environment.ProcessId);
        Assert.False(ScratchDirs.IsOwnedByThisProcess(dir), "precondition: transfer disowns first");

        Assert.True(ScratchDirs.AdoptIfHandedToThisProcess(dir));

        Assert.True(ScratchDirs.IsOwnedByThisProcess(dir),
            "an adopted directory must be deleted at this process's exit rather than leaking");
    }

    [Fact]
    public void Adopt_RefusesADirectoryOwnedBySomeoneElse()
    {
        // THE safety test. --merge-counts takes an arbitrary path, so "adopt the directory of
        // every carry file" would have this process delete a directory of the caller's at exit.
        // Adoption is gated on the sidecar already naming us, which a hand-passed path never does.
        var dir = MakeDir("someone-else");
        var other = LiveProcess();
        ScratchDirs.TransferOwnership(dir, other.Id);

        Assert.False(ScratchDirs.AdoptIfHandedToThisProcess(dir));
        Assert.False(ScratchDirs.IsOwnedByThisProcess(dir));
    }

    [Fact]
    public void Adopt_RefusesADirectoryWithNoSidecarAtAll()
    {
        // A plain directory the caller pointed --merge-counts into. Nothing about it says ours.
        var dir = Path.Combine(_root, "al-runner-resume-unmarked");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "attempt.xml"), "<testsuites/>");

        Assert.False(ScratchDirs.AdoptIfHandedToThisProcess(dir));
        Assert.False(ScratchDirs.IsOwnedByThisProcess(dir));
    }
}
