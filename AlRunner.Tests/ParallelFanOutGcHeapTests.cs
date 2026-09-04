// ParallelFanOutGcHeapTests — each --jobs worker is tuned for footprint (issues #2280, #2713).
//
// The runner ships with <ServerGarbageCollection>true</ServerGarbageCollection> (#2577), and
// Server GC sizes its heap count from the CORE count. That is the right call for a single
// process — #2577 measured 6.2% fewer instructions on a cold AL compile — but under --jobs it
// means every worker independently believes it owns all 12 cores and keeps an arena sized for
// a machine it does not have to itself.
//
// An earlier version of this file divided the core budget across workers (cores / jobs). That
// was a guess made before anything was measured, and the measurement does not support it: one
// heap per worker wins at EVERY job count, not only at high ones. Peak PSS of the whole process
// tree, sampled twice a second, on BC 28.1 with warm caches, 12 cores / 32 GB. The pass count is
// identical in every row of all three tables, so none of these knobs changes what the run
// reports.
//
//   Tests-SMB alone, one process, 1,027 tests, 259 passing throughout:
//     default (12 heaps)                 3,313 / 3,451 MB   106.8 s
//     1 heap                             1,675 MB           113.8 s
//     1 heap + GCConserveMemory=9        1,493 MB           113.3 s
//     1 heap + gcConcurrent=0            1,523 MB           112.1 s
//     1 heap + both                      1,370 MB           114.3 s
//
//   --jobs 2, 2 buckets, 1,861 tests, 455 passing both:
//     old configuration (6 heaps/worker)  4,983 MB   113.1 s
//     all three knobs                     2,434 MB   119.9 s
//
//   --jobs 4, 4 buckets, 3,881 tests, 925 passing both:
//     old configuration (3 heaps/worker)  8,183 MB   296.7 s
//     all three knobs                     4,193 MB   316.4 s
//
//   The two --jobs tables are a controlled A/B: same binary, same warm cache, the knobs set
//   through the override path so nothing but the knobs differs. An earlier attempt compared
//   across a rebuild, which invalidated the AL-output cache and moved the pass count from 873
//   to 925 for reasons unrelated to GC — both configurations report 925 on one warm cache.
//
// So about half the peak memory for 6-7% wall. That trade buys concurrency: at ~1.2 GB per worker
// the machine runs out of cores before it runs out of RAM, which is the opposite of the
// situation that OOMed this machine twice at four workers.
//
// WHY NOT A HEAP HARD LIMIT
//   DOTNET_GCHeapHardLimit recovers a similar amount and has a cliff. Below about 1.25 GB on
//   Tests-SMB the run silently drops from 259 to 212 passing, with no error and an unchanged
//   exit code, because an OutOfMemoryException in the table-extension symbol parse is swallowed
//   (#2712). Every knob chosen here is soft: it can cost time, never correctness.
//
// WHY LIVE RETENTION IS NOT THE TARGET
//   AL_RUNNER_MEM_CENSUS=1 forces a blocking full GC after every test. Across all 1,027 tests
//   live retention stays flat at 214-398 MB and daSources never drops. Nothing accumulates —
//   peak RSS is roughly 90% allocator arena, which is why a soft knob recovers so much of it.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class ParallelFanOutGcHeapTests
{
    /// <summary>One heap per worker, whatever the machine and whatever the job count. Measured
    /// at --jobs 2 and --jobs 4 (see the header); the old cores/jobs division handed a worker 6
    /// heaps at --jobs 2 and cost 2.5 GB of peak to save 6.8 s of wall.</summary>
    [Theory]
    [InlineData(12, 2)]
    [InlineData(12, 4)]
    [InlineData(12, 6)]
    [InlineData(12, 12)]
    [InlineData(8, 2)]
    [InlineData(4, 8)]
    public void GcHeapCountForWorker_IsOneForEveryFanOut(int cores, int jobs)
        => Assert.Equal(1, ParallelFanOut.GcHeapCountForWorker(cores, jobs));

    /// <summary>Never zero: a heap count of 0 is not a valid GC configuration, and more workers
    /// than cores is an ordinary thing to ask for.</summary>
    [Theory]
    [InlineData(4, 8)]
    [InlineData(2, 12)]
    [InlineData(1, 1)]
    public void GcHeapCountForWorker_IsNeverBelowOne(int cores, int jobs)
        => Assert.True(ParallelFanOut.GcHeapCountForWorker(cores, jobs) >= 1);

    /// <summary>The knobs actually reach the worker, and under the exact names the CLR reads —
    /// the whole measurement in the header is meaningless if the child never sees them.</summary>
    [Fact]
    public void WorkerEnvironment_SetsAllThreeFootprintKnobs()
    {
        var env = ParallelFanOut.WorkerEnvironment(cores: 12, jobs: 6);

        Assert.True(env.TryGetValue("DOTNET_GCHeapCount", out var heaps));
        Assert.Equal("1", heaps);

        Assert.True(env.TryGetValue("DOTNET_GCConserveMemory", out var conserve));
        Assert.Equal("9", conserve);

        // 0 = background GC off. A background collection keeps its own budget alive, which is
        // 150 MB of the measured difference on Tests-SMB.
        Assert.True(env.TryGetValue("DOTNET_gcConcurrent", out var concurrent));
        Assert.Equal("0", concurrent);
    }

    /// <summary>Negative: an explicit user setting wins, for each knob independently. Someone
    /// tuning one of these by hand — or a CI runner that sets one globally — must not be
    /// silently overridden, and setting one must not suppress the other two.</summary>
    [Fact]
    public void WorkerEnvironment_DefersToAnExplicitHeapCount()
    {
        var env = ParallelFanOut.WorkerEnvironment(cores: 12, jobs: 6, userHeapCount: "8");

        Assert.False(env.ContainsKey("DOTNET_GCHeapCount"),
            "an explicit DOTNET_GCHeapCount must be left alone, not replaced by the computed one");
        Assert.True(env.ContainsKey("DOTNET_GCConserveMemory"),
            "setting one knob by hand must not suppress the others");
        Assert.True(env.ContainsKey("DOTNET_gcConcurrent"));
    }

    [Fact]
    public void WorkerEnvironment_DefersToAnExplicitConserveMemory()
    {
        var env = ParallelFanOut.WorkerEnvironment(cores: 12, jobs: 6, userConserveMemory: "3");

        Assert.False(env.ContainsKey("DOTNET_GCConserveMemory"),
            "an explicit DOTNET_GCConserveMemory must be left alone");
        Assert.True(env.ContainsKey("DOTNET_GCHeapCount"));
        Assert.True(env.ContainsKey("DOTNET_gcConcurrent"));
    }

    [Fact]
    public void WorkerEnvironment_DefersToAnExplicitGcConcurrent()
    {
        var env = ParallelFanOut.WorkerEnvironment(cores: 12, jobs: 6, userGcConcurrent: "1");

        Assert.False(env.ContainsKey("DOTNET_gcConcurrent"),
            "an explicit DOTNET_gcConcurrent must be left alone — someone who wants background "
            + "GC back must be able to ask for it");
        Assert.True(env.ContainsKey("DOTNET_GCHeapCount"));
        Assert.True(env.ContainsKey("DOTNET_GCConserveMemory"));
    }
}
