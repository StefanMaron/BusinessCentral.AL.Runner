// CacheCompileLockTests — #5238: the gate that makes one process compile a cache key while the
// others wait and take the HIT. The two-process proof is CacheCompileLockEndToEndTests; these pin
// the contract of the lock itself, which that test cannot reach: the bounded wait, the loud
// degraded states, and that a lock really is released.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class CacheCompileLockTests : IDisposable
{
    private readonly string _dir = TestScratch.FlatDir("al-runner-cache-lock-unit-");

    public CacheCompileLockTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string LockPath(string key = "k1") => Path.Combine(_dir, key + ".lock");

    private static readonly TimeSpan Long = TimeSpan.FromSeconds(20);

    [Fact]
    public void Uncontended_TakesTheLockWithoutWaiting()
    {
        var said = new List<string>();
        using var gate = CacheCompileLock.Acquire(LockPath(), "thing", Long, said.Add);

        Assert.True(gate.HoldsLock);
        Assert.False(gate.WaitedForSibling);
        Assert.Null(gate.DegradedReason);
        Assert.Empty(said);
    }

    /// <summary>The whole mechanism: a second asker does not get the lock until the holder lets go,
    /// and then does, having waited.</summary>
    [Fact]
    public void ASecondAcquire_WaitsForTheHolder_ThenTakesTheLock()
    {
        var first = CacheCompileLock.Acquire(LockPath(), "thing", Long, _ => { });
        var second = BlockedWaiter.StartUntilBlocked(
            () => CacheCompileLock.Acquire(LockPath(), "thing", Long, _ => { }), Long);

        Assert.False(second.Wait(TimeSpan.FromMilliseconds(600)),
            "the second acquire returned while the first still held the lock");

        first.Dispose();
        Assert.True(second.Wait(TimeSpan.FromSeconds(30)), "the second acquire never saw the release");
        using var gate = second.Result;
        Assert.True(gate.HoldsLock);
        Assert.True(gate.WaitedForSibling);
        Assert.Null(gate.DegradedReason);
    }

    /// <summary>Release is real: after a holder disposes, the next asker takes the lock at once and
    /// has not waited. A lock that stayed held would read here as a wait.</summary>
    [Fact]
    public void AfterRelease_TheNextAcquireDoesNotWait()
    {
        CacheCompileLock.Acquire(LockPath(), "thing", Long, _ => { }).Dispose();

        using var gate = CacheCompileLock.Acquire(LockPath(), "thing", Long, _ => { });
        Assert.True(gate.HoldsLock);
        Assert.False(gate.WaitedForSibling);
        Assert.True(File.Exists(LockPath()), "the lock file must stay: deleting it races a process that already opened it");
    }

    /// <summary>The wait is bounded and its end is loud: the line names the limit and the variable
    /// that moves it, and the result excludes nobody (it holds no lock), so the caller compiles.</summary>
    [Fact]
    public void TheWaitIsBounded_AndExpiryIsLoud()
    {
        var said = new List<string>();
        using var holder = CacheCompileLock.Acquire(LockPath(), "thing", Long, _ => { });

        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var gate = CacheCompileLock.Acquire(LockPath(), "the thing", TimeSpan.FromMilliseconds(400), said.Add);
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"the bounded wait took {clock.Elapsed}");
        Assert.False(gate.HoldsLock);
        Assert.True(gate.WaitedForSibling);
        Assert.NotNull(gate.DegradedReason);
        var line = Assert.Single(said);
        Assert.Contains("the thing", line, StringComparison.Ordinal);
        Assert.Contains(CacheCompileLock.WaitSecEnvVar, line, StringComparison.Ordinal);
        Assert.Contains("compiling it here", line, StringComparison.Ordinal);

        // The expiry took nothing from the holder: it still holds, so another asker still waits.
        using var third = CacheCompileLock.Acquire(LockPath(), "thing", TimeSpan.FromMilliseconds(300), _ => { });
        Assert.False(third.HoldsLock);
    }

    /// <summary>A lock that cannot be used for a reason other than "held" must not look like a
    /// wait that expired, nor like a free lock: it is named and compiles without. A path under a
    /// FILE is such a reason, and it must return at once rather than poll to the limit.</summary>
    [Fact]
    public void AnUnusableLockPath_IsNamedAndDoesNotWait()
    {
        var blocker = Path.Combine(_dir, "afile");
        File.WriteAllText(blocker, "x");
        var said = new List<string>();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var gate = CacheCompileLock.Acquire(Path.Combine(blocker, "k.lock"), "the thing", Long, said.Add);
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}: it polled instead of refusing");
        Assert.False(gate.HoldsLock);
        Assert.False(gate.WaitedForSibling);
        var line = Assert.Single(said);
        Assert.Contains("could not take the compile lock", line, StringComparison.Ordinal);
        Assert.Contains("the thing", line, StringComparison.Ordinal);
        Assert.NotNull(gate.DegradedReason);
    }

    /// <summary>The same for a lock path that is a directory: the open fails, and it is not "held".</summary>
    [Fact]
    public void ALockPathThatIsADirectory_IsNamedAndDoesNotWait()
    {
        var asDir = Path.Combine(_dir, "k.lock");
        Directory.CreateDirectory(asDir);
        var said = new List<string>();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var gate = CacheCompileLock.Acquire(asDir, "the thing", Long, said.Add);
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}: it polled instead of refusing");
        Assert.False(gate.HoldsLock);
        Assert.Contains("could not take the compile lock", Assert.Single(said), StringComparison.Ordinal);
    }

    /// <summary>Keys do not share a lock: compiling one dependency never holds up another.</summary>
    [Fact]
    public void DifferentKeys_DoNotBlockEachOther()
    {
        using var a = CacheCompileLock.Acquire(LockPath("a"), "a", Long, _ => { });
        using var b = CacheCompileLock.Acquire(LockPath("b"), "b", Long, _ => { });

        Assert.True(a.HoldsLock);
        Assert.True(b.HoldsLock);
        Assert.False(b.WaitedForSibling);
    }

    /// <summary>A wait long enough to look like a hang says what it is waiting for (and a short one
    /// says nothing, so a one-second dependency does not print a line).</summary>
    [Fact]
    public void ALongWait_IsAnnouncedOnce_AShortOneIsNot()
    {
        var holder = CacheCompileLock.Acquire(LockPath(), "thing", Long, _ => { });
        var quiet = new List<string>();
        var shortWait = BlockedWaiter.StartUntilBlocked(() => CacheCompileLock.Acquire(LockPath(), "thing", Long, quiet.Add), Long);
        Thread.Sleep(300);
        holder.Dispose();
        using (shortWait.Result) { }
        Assert.Empty(quiet);

        var holder2 = CacheCompileLock.Acquire(LockPath(), "the slow thing", Long, _ => { });
        var loud = new List<string>();
        var longWait = Task.Run(() => CacheCompileLock.Acquire(LockPath(), "the slow thing", Long, loud.Add));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            lock (loud) if (loud.Count > 0) break;
            Thread.Sleep(100);
        }
        Assert.Contains("the slow thing", Assert.Single(loud), StringComparison.Ordinal);
        holder2.Dispose();
        using (longWait.Result) { }
        Assert.Single(loud);
    }

    [Theory]
    [InlineData(null, 1800)]
    [InlineData("", 1800)]
    [InlineData("45", 45)]
    public void TheEnvironmentWait_DefaultsAndOverrides(string? raw, int expectedSeconds)
    {
        var saved = Environment.GetEnvironmentVariable(CacheCompileLock.WaitSecEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(CacheCompileLock.WaitSecEnvVar, raw);
            var said = new List<string>();
            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), CacheCompileLock.MaxWaitFromEnvironment(said.Add));
            Assert.Empty(said);
        }
        finally { Environment.SetEnvironmentVariable(CacheCompileLock.WaitSecEnvVar, saved); }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("10m")]
    [InlineData("1.5")]
    public void AnUnusableEnvironmentWait_IsNamedAndTheDefaultUsed(string raw)
    {
        var saved = Environment.GetEnvironmentVariable(CacheCompileLock.WaitSecEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(CacheCompileLock.WaitSecEnvVar, raw);
            var said = new List<string>();
            Assert.Equal(CacheCompileLock.DefaultMaxWait, CacheCompileLock.MaxWaitFromEnvironment(said.Add));
            var line = Assert.Single(said);
            Assert.Contains($"'{raw}'", line, StringComparison.Ordinal);
        }
        finally { Environment.SetEnvironmentVariable(CacheCompileLock.WaitSecEnvVar, saved); }
    }

    /// <summary>The classifier that decides "wait" against "give up" is checked against the real
    /// exception a conflicting open raises on this platform, not against a constructed one.</summary>
    [Fact]
    public void ARealSharingViolation_IsHeldElsewhere_AndOtherIoErrorsAreNot()
    {
        using var holder = new FileStream(LockPath("p"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1);
        var conflict = Record.Exception(() =>
            new FileStream(LockPath("p"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1));

        Assert.NotNull(conflict);
        Assert.True(CacheCompileLock.IsHeldElsewhere(conflict!), $"{conflict.GetType().FullName} hresult={conflict.HResult}");
        Assert.False(CacheCompileLock.IsHeldElsewhere(new IOException("disk full", 28)));
        Assert.False(CacheCompileLock.IsHeldElsewhere(new DirectoryNotFoundException("gone")));
        Assert.False(CacheCompileLock.IsHeldElsewhere(new UnauthorizedAccessException("denied")));
    }

    /// <summary>The claim that makes a stale lock impossible: the lock belongs to the process, so a
    /// holder that is killed (the incident class this repository has met: a run killed mid-write
    /// leaving a cache inconsistent) frees it, and another PROCESS is excluded while it lives. The
    /// holder here is util-linux flock(1), an implementation independent of this one.</summary>
    [SkippableFact]
    public void AKilledHolderProcess_LeavesNoStaleLock_AndALiveOneExcludesUs()
    {
        Skip.IfNot(OperatingSystem.IsLinux() && File.Exists("/usr/bin/flock"), "needs util-linux flock(1)");

        var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/flock")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "-x", LockPath("child"), "sleep", "300" }) psi.ArgumentList.Add(a);
        using var holder = System.Diagnostics.Process.Start(psi)!;
        try
        {
            // Wait until the child really holds it: we are refused for as long as it lives.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            CacheCompileLock refused;
            do
            {
                refused = CacheCompileLock.Acquire(LockPath("child"), "thing", TimeSpan.FromMilliseconds(300), _ => { });
                if (!refused.HoldsLock) break;
                refused.Dispose();
                Thread.Sleep(100);
            } while (DateTime.UtcNow < deadline);
            Assert.False(refused.HoldsLock, "a live process holding the lock did not exclude us");
            Assert.True(refused.WaitedForSibling);

            holder.Kill(entireProcessTree: true);
            holder.WaitForExit();

            using var after = CacheCompileLock.Acquire(LockPath("child"), "thing", TimeSpan.FromSeconds(30), _ => { });
            // A moment of "held" is allowed: the tree's last process may still be closing the
            // descriptor. What matters is that the lock comes free without anyone releasing it.
            Assert.True(after.HoldsLock, "a killed holder left its lock behind");
        }
        finally
        {
            try { if (!holder.HasExited) holder.Kill(entireProcessTree: true); } catch { }
        }
    }
}
