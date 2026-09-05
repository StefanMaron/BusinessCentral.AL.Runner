// ParallelFanOutWorkerExitTests — issue #2704, the --jobs half.
//
// ParallelFanOut.Run used to wait on each worker with a bare `p.WaitForExit()`. One worker
// that finished its run but never exited (#2704: a foreground BC thread outliving Main) hung
// the whole aggregate — no diagnostic, no partial results, even though every other worker's
// output was already sitting in the parent. The kill decision is a pure function so this can
// assert the policy without spawning processes: a worker gets a bounded grace period AFTER
// its JUnit file appears (that file is written after the summary, so it is the "work is done"
// signal — stdout EOF cannot be, since a hung child never closes its pipes), and an unbounded
// wait BEFORE it (a genuinely long shard is --test-timeout's business, not this one's).

using System.Text.RegularExpressions;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class ParallelFanOutWorkerExitTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);

    [Fact]
    public void BeforeTheJUnitFileExists_NeverKills()
    {
        Assert.False(ParallelFanOut.ShouldKillWorker(junitWritten: false, TimeSpan.Zero, Grace));
        Assert.False(ParallelFanOut.ShouldKillWorker(junitWritten: false, TimeSpan.FromHours(3), Grace));
    }

    [Fact]
    public void AfterTheJUnitFileExists_KillsOnlyOnceGraceIsUsedUp()
    {
        Assert.False(ParallelFanOut.ShouldKillWorker(junitWritten: true, TimeSpan.Zero, Grace));
        Assert.False(ParallelFanOut.ShouldKillWorker(junitWritten: true, Grace - TimeSpan.FromMilliseconds(1), Grace));
        Assert.True(ParallelFanOut.ShouldKillWorker(junitWritten: true, Grace, Grace));
        Assert.True(ParallelFanOut.ShouldKillWorker(junitWritten: true, Grace + TimeSpan.FromMinutes(5), Grace));
    }

    /// <summary>
    /// A killed worker's Process.ExitCode is the kill signal (-1 / 137), not a verdict. Its
    /// verdict is derivable from the JUnit file it already wrote: any failure or error → 1,
    /// otherwise 0 — the same mapping Program.cs applies to its own results.
    /// </summary>
    [Fact]
    public void KilledWorkerExitCode_ComesFromItsJUnitCounts()
    {
        Assert.Equal(0, ParallelFanOut.ExitCodeForKilledWorker(new JUnitTotals(12, 0, 0, 2)));
        Assert.Equal(1, ParallelFanOut.ExitCodeForKilledWorker(new JUnitTotals(12, 1, 0, 0)));
        Assert.Equal(1, ParallelFanOut.ExitCodeForKilledWorker(new JUnitTotals(12, 0, 1, 0)));
    }

    [Fact]
    public void WorkerExitGrace_IsLongerThanAnyRealShutdown()
    {
        // JUnit write → coverage/classification output → return is well under a second;
        // the grace only has to be long enough that no healthy worker is ever killed.
        Assert.InRange(ParallelFanOut.WorkerExitGrace, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Asserted against the source, the same way ParallelFanOutVisibilityTests does: the
    /// regression is a specific call shape, and spawning real workers to observe a hang would
    /// make the test itself hang on failure.
    /// </summary>
    [Fact]
    public void RunNeverWaitsOnAWorkerWithoutABound()
    {
        var src = Source();
        var bare = Regex.Matches(src, @"\.WaitForExit\(\s*\)").Select(m => m.Value).ToList();
        Assert.True(bare.Count == 0,
            "ParallelFanOut.cs waits on a worker with a bare WaitForExit() — one worker that " +
            "finishes but never exits (#2704) hangs the whole --jobs run:\n  " + string.Join("\n  ", bare));
    }

    private static string Source()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir != null; i++)
        {
            var p = Path.Combine(dir, "AlRunner", "Infrastructure", "ParallelFanOut.cs");
            if (File.Exists(p)) return File.ReadAllText(p);
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("could not locate AlRunner/Infrastructure/ParallelFanOut.cs");
    }
}
