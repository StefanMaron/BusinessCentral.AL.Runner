// ParallelFanOutEmitTimeoutTests — the AL emit timeout must scale with shard count (issue #2715).
//
// AL_RUNNER_EMIT_TIMEOUT_SEC (default 120s in Program.cs) is measured in wall-clock time, but
// under --jobs N every worker competes with N-1 others for the same cores. Measured on a
// 12-core box at --jobs 12: Tests-ERM's emit was cut off at 120.1s of wall while that worker's
// total wall was 820.1s — it was getting a fraction of a core. Tests-SCM failed the same way.
// Losing those two largest buckets took a 40,550-test run down to 14,856 (63% of the surface
// gone), reported as a plain total with no sign anything was lost.
//
// The fix follows the exact pattern #2713/#2714 established for the GC footprint knobs in
// ParallelFanOut.WorkerEnvironment: scale the default handed to a worker by the shard count
// when the user has not set the knob themselves, and leave an explicit user value alone.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class ParallelFanOutEmitTimeoutTests
{
    /// <summary>The worker gets DefaultEmitTimeoutSec scaled by the shard count, not the flat
    /// single-process default — a bundle sharing cores with N-1 other workers can take roughly
    /// N times as long in wall time to reach the same amount of CPU work.</summary>
    [Theory]
    [InlineData(1, ParallelFanOut.DefaultEmitTimeoutSec)]
    [InlineData(2, ParallelFanOut.DefaultEmitTimeoutSec * 2)]
    [InlineData(6, ParallelFanOut.DefaultEmitTimeoutSec * 6)]
    [InlineData(12, ParallelFanOut.DefaultEmitTimeoutSec * 12)]
    public void EmitTimeoutSecForWorker_ScalesByShardCount(int jobs, int expected)
        => Assert.Equal(expected, ParallelFanOut.EmitTimeoutSecForWorker(jobs));

    /// <summary>Never scaled down to zero or below the single-process default — a shard count of
    /// 0 must not be reached in practice, but the formula must not misbehave if it is.</summary>
    [Fact]
    public void EmitTimeoutSecForWorker_NeverBelowTheSingleProcessDefault()
        => Assert.True(ParallelFanOut.EmitTimeoutSecForWorker(0) >= ParallelFanOut.DefaultEmitTimeoutSec);

    /// <summary>Positive: when the user has not set AL_RUNNER_EMIT_TIMEOUT_SEC, the worker
    /// environment carries the shard-scaled value under the exact name Program.cs reads.</summary>
    [Fact]
    public void WorkerEnvironment_ScalesEmitTimeoutWhenUserSetNothing()
    {
        var env = ParallelFanOut.WorkerEnvironment(cores: 12, jobs: 6);

        Assert.True(env.TryGetValue("AL_RUNNER_EMIT_TIMEOUT_SEC", out var value));
        Assert.Equal((ParallelFanOut.DefaultEmitTimeoutSec * 6).ToString(), value);
    }

    /// <summary>Negative: an explicit AL_RUNNER_EMIT_TIMEOUT_SEC set by the user (by hand, or by
    /// a CI runner setting one globally) must win over the scaled default and must not be
    /// present in the override dictionary at all — the child inherits the real environment
    /// variable already, exactly as the GC knobs do (ParallelFanOutGcHeapTests).</summary>
    [Fact]
    public void WorkerEnvironment_DefersToAnExplicitEmitTimeout()
    {
        var env = ParallelFanOut.WorkerEnvironment(cores: 12, jobs: 6, userEmitTimeoutSec: "300");

        Assert.False(env.ContainsKey("AL_RUNNER_EMIT_TIMEOUT_SEC"),
            "an explicit AL_RUNNER_EMIT_TIMEOUT_SEC must be left alone, not replaced by the scaled one");
        // Setting the emit-timeout knob must not suppress the unrelated GC knobs.
        Assert.True(env.ContainsKey("DOTNET_GCHeapCount"));
        Assert.True(env.ContainsKey("DOTNET_GCConserveMemory"));
        Assert.True(env.ContainsKey("DOTNET_gcConcurrent"));
    }

    /// <summary>Setting one of the pre-existing GC knobs must not suppress the emit-timeout
    /// scaling — each knob is independent, the same invariant ParallelFanOutGcHeapTests already
    /// pins for the other three.</summary>
    [Fact]
    public void WorkerEnvironment_ExplicitGcKnobDoesNotSuppressEmitTimeoutScaling()
    {
        var env = ParallelFanOut.WorkerEnvironment(cores: 12, jobs: 6, userHeapCount: "8");

        Assert.True(env.TryGetValue("AL_RUNNER_EMIT_TIMEOUT_SEC", out var value));
        Assert.Equal((ParallelFanOut.DefaultEmitTimeoutSec * 6).ToString(), value);
    }

    /// <summary>CountOccurrences drives the aggregate's "bundles not run" line — a run that
    /// loses a bundle to COMPILE FAIL must not report the smaller total as though it were
    /// complete (#2715's own complaint about the aggregate summary).</summary>
    [Fact]
    public void CountOccurrences_CountsEachCompileFailMarker()
    {
        var stdout =
            "jobs: 2 bundle(s) across 2 worker process(es)\n" +
            "=== Tests-ERM — COMPILE FAIL ===\n" +
            "  <bundled>: EMIT-TIMEOUT after 120s\n";

        Assert.Equal(1, ParallelFanOut.CountOccurrences(stdout, " — COMPILE FAIL ==="));
    }

    /// <summary>Negative: a clean run's output — no COMPILE FAIL marker anywhere — counts zero,
    /// so the aggregate's "bundles not run" line stays silent instead of always printing.</summary>
    [Fact]
    public void CountOccurrences_IsZeroWhenNoBundleFailedToCompile()
    {
        var stdout = "jobs: 2 bundle(s) across 2 worker process(es)\nTests: 100 total\n";

        Assert.Equal(0, ParallelFanOut.CountOccurrences(stdout, " — COMPILE FAIL ==="));
    }

    /// <summary>Multiple failed bundles in the same shard's captured output are all counted, not
    /// just the first — a worker can process more than one bundle per shard.</summary>
    [Fact]
    public void CountOccurrences_CountsMultipleOccurrences()
    {
        var stdout =
            "=== Tests-ERM — COMPILE FAIL ===\n" +
            "=== Tests-SCM — COMPILE FAIL ===\n";

        Assert.Equal(2, ParallelFanOut.CountOccurrences(stdout, " — COMPILE FAIL ==="));
    }
}
