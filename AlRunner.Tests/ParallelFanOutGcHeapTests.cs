// ParallelFanOutGcHeapTests — each --jobs worker gets its own GC heap budget (issue #2280).
//
// The runner ships with <ServerGarbageCollection>true</ServerGarbageCollection> (#2577), and
// Server GC sizes its heap count from the CORE count. That is the right call for a single
// process — #2577 measured 6.2% fewer instructions on a cold AL compile — but under --jobs it
// means every worker independently believes it owns all 12 cores and allocates 12 heaps.
//
// Measured on Tests-SMB (1,027 tests), two runs each, the same bucket and binary:
//
//   Server GC, default heaps   3,244 / 3,119 MB   106 / 107 s
//   Server GC, 2 heaps         2,090 / 2,192 MB   108 / 110 s
//   Workstation GC             1,591 / 1,561 MB   120 / 122 s
//
// So capping heaps costs about 2% wall and returns about 34% of peak RSS. Since worker count is
// bounded by RAM rather than cores (see ShardPlanner), that trade buys more concurrency than it
// costs. Workstation GC returns more still but costs ~14% wall, so it is not the default here.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class ParallelFanOutGcHeapTests
{
    /// <summary>The core budget is shared out, not handed to each worker whole.</summary>
    [Theory]
    [InlineData(12, 6, 2)]
    [InlineData(12, 4, 3)]
    [InlineData(12, 3, 4)]
    [InlineData(8, 2, 4)]
    public void GcHeapCountForWorker_DividesCoresAcrossWorkers(int cores, int jobs, int expected)
        => Assert.Equal(expected, ParallelFanOut.GcHeapCountForWorker(cores, jobs));

    /// <summary>Never zero: a heap count of 0 is not a valid GC configuration, and more workers
    /// than cores is an ordinary thing to ask for when the limit is memory rather than CPU.</summary>
    [Theory]
    [InlineData(4, 8)]
    [InlineData(2, 12)]
    [InlineData(1, 1)]
    public void GcHeapCountForWorker_IsNeverBelowOne(int cores, int jobs)
        => Assert.True(ParallelFanOut.GcHeapCountForWorker(cores, jobs) >= 1);

    /// <summary>Never more than the machine has — handing a worker more heaps than cores is the
    /// bug this exists to prevent, in the other direction.</summary>
    [Theory]
    [InlineData(12, 1)]
    [InlineData(4, 1)]
    public void GcHeapCountForWorker_NeverExceedsTheCoreCount(int cores, int jobs)
        => Assert.True(ParallelFanOut.GcHeapCountForWorker(cores, jobs) <= cores);

    /// <summary>The knob actually reaches the worker, and as DOTNET_GCHeapCount specifically —
    /// the whole measurement above is meaningless if the child never sees it.</summary>
    [Fact]
    public void WorkerEnvironment_SetsTheHeapCount()
    {
        var env = ParallelFanOut.WorkerEnvironment(cores: 12, jobs: 6);

        Assert.True(env.TryGetValue("DOTNET_GCHeapCount", out var v));
        Assert.Equal("2", v);
    }

    /// <summary>Negative: an explicit user setting wins. Someone tuning DOTNET_GCHeapCount by
    /// hand — or a CI runner that sets it globally — must not be silently overridden.</summary>
    [Fact]
    public void WorkerEnvironment_DefersToAnExplicitUserSetting()
    {
        var env = ParallelFanOut.WorkerEnvironment(cores: 12, jobs: 6, userHeapCount: "8");

        Assert.False(env.ContainsKey("DOTNET_GCHeapCount"),
            "an explicit DOTNET_GCHeapCount must be left alone, not replaced by the computed one");
    }
}
