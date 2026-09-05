// ScratchDirsRunnerStartupTests — the runner sweeps stale scratch directories out of the OS
// temp root when it starts (issue #2706).
//
// Every per-process / per-run scratch directory the runner writes under Path.GetTempPath()
// (dependency extraction, the report engine's per-session temp folder, --jobs shard reports,
// --no-cache throwaway roots, resume carry files, ...) used to be removed only by the process
// that created it, at its own clean exit — so a run that was killed, OOM'd, or hit the
// watchdog left its directory behind forever. Measured on the reporting machine:
// al-runner-navserver/user/ alone held 212 per-pid folders, 211 of them for dead processes,
// 7.5 GB. On a stock Linux desktop /tmp is a tmpfs, so that is RAM, charged to no process.
//
// The runner cannot know at exit time that it is about to be killed, so the only place the
// leftovers of a dead process can be reclaimed is the start of the NEXT process. This class
// plants the shapes that leak, spawns the real runner once, and asserts which ones survive:
//
//   - a directory whose `.owner` sidecar names a process that no longer exists -> removed
//   - the legacy pid-named leaves (al-runner-deps/p<pid>, al-runner-navserver/user/<pid>)
//     whose pid is dead -> removed
//   - a directory whose owner is THIS test host (alive) -> kept, untouched, INCLUDING when the
//     recorded wall-clock start time has drifted from the one the runner computes for this pid
//     (see the ScratchDirs header: that drift is real, grows with the owner's age, and jumps for
//     every sidecar at once on a clock step)
//   - an unmarked directory with no pid in its name -> kept: the sweep never guesses by age,
//     because several runners share one TMPDIR on a busy machine and an age heuristic is the
//     only rule that can delete a live process's directory out from under it
//
// The spawned run needs no BC artifacts: --bc-version 1.2.3.4 --no-auto-provision fails at
// artifact selection, which is after the sweep and before any BC type loads. The planted
// directories are all created by this test and named with a fresh GUID, so the test never
// touches another process's scratch space.

using System.Diagnostics;
using System.Text;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public class ScratchDirsRunnerStartupTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    /// <summary>The sidecar format the runner's sweep reads. Written by hand here (rather than
    /// through the runner's own helper) so the format itself is pinned as a contract — a
    /// leftover from a killed run must still be recognised by a later build.</summary>
    private static void WriteOwnerMarker(string dir, int pid, long startTicks, long? startJiffies = null)
        => File.WriteAllText(dir + ".owner",
            $"pid={pid}\nstart={startTicks}\n" + (startJiffies is long j ? $"startjiffies={j}\n" : ""));

    /// <summary>A pid that no process on this machine has. Linux pid_max tops out at
    /// 4,194,304 and Windows pids are far smaller in practice, so counting down from two
    /// billion finds one on the first try; the loop is only there so the claim is checked
    /// rather than assumed.</summary>
    internal static int FindDeadPid()
    {
        for (var pid = 2_000_000_000; pid > 1_000_000_000; pid -= 7919)
        {
            try { using var _ = Process.GetProcessById(pid); }
            catch (ArgumentException) { return pid; }
            catch (InvalidOperationException) { return pid; }
        }
        throw new InvalidOperationException("could not find a free pid");
    }

    private static (int Exit, string Output) RunRunnerWithoutArtifacts()
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(" --bc-version 1.2.3.4 --no-auto-provision");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(120_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (p.ExitCode, sb.ToString());
    }

    [Fact]
    public void RunnerStartup_RemovesDeadOwnersLeftovers_AndLeavesLiveAndUnmarkedOnesAlone()
    {
        var temp = Path.GetTempPath();
        var deadPid = FindDeadPid();
        var me = Process.GetCurrentProcess();
        var myStart = me.StartTime.ToUniversalTime().Ticks;
        var tag = Guid.NewGuid().ToString("N");

        // Shapes that must be reclaimed.
        var deadMarked = Path.Combine(temp, "al-runner-startup-sweep-tests", tag + "-dead");
        Directory.CreateDirectory(deadMarked);
        File.WriteAllText(Path.Combine(deadMarked, "payload.bin"), "x");
        WriteOwnerMarker(deadMarked, deadPid, 1);

        var deadFlatMarked = Path.Combine(temp, "al-runner-startup-sweep-flat-" + tag);
        Directory.CreateDirectory(deadFlatMarked);
        WriteOwnerMarker(deadFlatMarked, deadPid, 1);

        var legacyDeps = Path.Combine(temp, "al-runner-deps", "p" + deadPid);
        Directory.CreateDirectory(Path.Combine(legacyDeps, "Microsoft_Tests-TestLibraries_1_0"));
        File.WriteAllText(Path.Combine(legacyDeps, "Microsoft_Tests-TestLibraries_1_0", "X.al"), "codeunit 1 X {}");

        var legacyNavUser = Path.Combine(temp, "al-runner-navserver", "user", deadPid.ToString());
        Directory.CreateDirectory(Path.Combine(legacyNavUser, "TEMP"));
        File.WriteAllText(Path.Combine(legacyNavUser, "TEMP", "GU00000001.xlsx"), "x");

        // Shapes that must survive.
        var liveMarked = Path.Combine(temp, "al-runner-startup-sweep-tests", tag + "-live");
        Directory.CreateDirectory(liveMarked);
        File.WriteAllText(Path.Combine(liveMarked, "payload.bin"), "x");
        WriteOwnerMarker(liveMarked, me.Id, myStart);

        // Same live owner, but with a wall-clock start time ten seconds off what the runner will
        // compute for this pid — days of ordinary drift, or one clock step. The boot-relative value
        // it also carries is what makes this survivable.
        var liveSkewed = Path.Combine(temp, "al-runner-startup-sweep-tests", tag + "-live-skewed");
        Directory.CreateDirectory(liveSkewed);
        File.WriteAllText(Path.Combine(liveSkewed, "payload.bin"), "x");
        WriteOwnerMarker(liveSkewed, me.Id, myStart + 10 * TimeSpan.TicksPerSecond,
            ScratchDirs.TryReadStartJiffies(me.Id));

        var unmarked = Path.Combine(temp, "al-runner-startup-sweep-tests", tag + "-unmarked");
        Directory.CreateDirectory(unmarked);
        File.WriteAllText(Path.Combine(unmarked, "payload.bin"), "x");
        // Old by any age heuristic — and still not the sweep's to delete.
        Directory.SetLastWriteTimeUtc(unmarked, DateTime.UtcNow.AddDays(-30));

        try
        {
            var (exit, output) = RunRunnerWithoutArtifacts();
            Assert.True(exit != 0, "a run pinned to a version that cannot exist must not report success:\n" + output);

            Assert.False(Directory.Exists(deadMarked), $"dead-owner scratch dir survived the runner's startup sweep: {deadMarked}\n{output}");
            Assert.False(File.Exists(deadMarked + ".owner"), "dead-owner marker survived");
            Assert.False(Directory.Exists(deadFlatMarked), $"dead-owner flat scratch dir survived: {deadFlatMarked}");
            Assert.False(Directory.Exists(legacyDeps), $"legacy al-runner-deps/p<deadpid> survived: {legacyDeps}");
            Assert.False(Directory.Exists(legacyNavUser), $"legacy al-runner-navserver/user/<deadpid> survived: {legacyNavUser}");

            Assert.True(File.Exists(Path.Combine(liveMarked, "payload.bin")), "a LIVE owner's scratch dir was deleted");
            Assert.True(File.Exists(liveMarked + ".owner"), "a LIVE owner's marker was deleted");
            Assert.True(File.Exists(Path.Combine(liveSkewed, "payload.bin")),
                "a LIVE owner's scratch dir was deleted because its recorded wall-clock start time had drifted");
            Assert.True(File.Exists(Path.Combine(unmarked, "payload.bin")), "an unmarked directory was deleted — the sweep must not guess by age");
        }
        finally
        {
            foreach (var d in new[] { deadMarked, deadFlatMarked, legacyDeps, legacyNavUser, liveMarked, liveSkewed, unmarked })
            {
                try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { }
                try { File.Delete(d + ".owner"); } catch { }
            }
        }
    }
}
