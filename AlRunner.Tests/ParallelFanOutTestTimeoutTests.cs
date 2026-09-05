// ParallelFanOutTestTimeoutTests — the per-test watchdog must scale with shard count (issue #2718).
//
// AL_RUNNER_TEST_TIMEOUT_SEC (default 60s, TestExecutor.DefaultTestTimeoutSeconds) is wall-clock
// time on the AL execution thread, and under --jobs N every worker competes with N-1 others for
// the same cores. Same shape as the emit timeout #2715 fixed, but it needed measuring rather than
// assuming, because the two have very different margins:
//
//   emit timeout, the DEMONSTRATED failure: Tests-ERM's emit was cut off at 120.1s against a 120s
//   budget — about 1.0x of headroom, so contention broke it immediately and cost 63% of a
//   40,550-test run.
//
//   per-test watchdog, MEASURED here on a 12-core box (load ~4), single process, slowest SINGLE
//   test in each surface:
//       al-language corpus (2,464 tests):    0.78s ->  77x headroom to 60s
//       Tests-SMB, a BaseApp bucket (1,027): 4.36s -> 13.8x headroom to 60s
//
// So the corpus cannot reach this watchdog through contention at any realistic job count, and
// BaseApp is the surface that can. Against the ~7x stretch #2715 measured at --jobs 12 (not the
// 12x a linear model predicts), 4.36s lands near 30s — about half the budget. This is therefore
// a PLAUSIBLE failure with roughly 2x of room left, not a demonstrated one, and this file says so
// rather than implying the two cases are equivalent.
//
// It is fixed anyway because the Tests-SMB figure is a LOWER bound — measured without
// --test-data, where most tests fail early and stop short of the work they exist to do (234 of
// 1,027 passed) — and because the costs are lopsided: a spurious abort takes out the rest of its
// codeunit AND every later codeunit in the bundle (one such abort cost 6,097 tests across 189
// codeunits), while over-scaling costs only bounded extra wall on a genuine hang, which still
// gets caught and still reports as a timeout.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class ParallelFanOutTestTimeoutTests
{
    /// <summary>ParallelFanOut.DefaultTestTimeoutSec exists so this file can scale the same
    /// number TestExecutor actually falls back to. If they drift, workers get a budget derived
    /// from a default nothing uses and the scaling silently stops meaning what it says — so the
    /// private constant is read by reflection and compared, rather than trusted to stay in
    /// step by convention.</summary>
    [Fact]
    public void DefaultTestTimeoutSec_MatchesTheOneTestExecutorActuallyUses()
    {
        var field = typeof(TestExecutor).GetField(
            "DefaultTestTimeoutSeconds",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.True(field is not null,
            "TestExecutor.DefaultTestTimeoutSeconds was renamed or removed — ParallelFanOut."
            + "DefaultTestTimeoutSec is a copy of it and must be updated with it");

        Assert.Equal((int)field!.GetRawConstantValue()!, ParallelFanOut.DefaultTestTimeoutSec);
    }

    /// <summary>The worker gets DefaultTestTimeoutSec scaled by the shard count, matching the
    /// emit timeout's pattern: a test sharing cores with N-1 other workers can take roughly N
    /// times as long in wall time to do the same CPU work.</summary>
    [Theory]
    [InlineData(1, ParallelFanOut.DefaultTestTimeoutSec)]
    [InlineData(2, ParallelFanOut.DefaultTestTimeoutSec * 2)]
    [InlineData(6, ParallelFanOut.DefaultTestTimeoutSec * 6)]
    [InlineData(12, ParallelFanOut.DefaultTestTimeoutSec * 12)]
    public void TestTimeoutSecForWorker_ScalesByShardCount(int jobs, int expected)
        => Assert.Equal(expected, ParallelFanOut.TestTimeoutSecForWorker(jobs));

    /// <summary>Never scaled below the single-process default — a shard count of 0 should not be
    /// reached, but the formula must not hand a worker a SHORTER budget than it would get alone,
    /// which would turn this fix into the defect it exists to prevent.</summary>
    [Fact]
    public void TestTimeoutSecForWorker_NeverBelowTheSingleProcessDefault()
        => Assert.True(ParallelFanOut.TestTimeoutSecForWorker(0) >= ParallelFanOut.DefaultTestTimeoutSec);

    /// <summary>The scaled value must stay far above any legitimate test even at low job counts.
    /// The slowest test measured on either surface was 4.36s (Tests-SMB); at --jobs 2 the budget
    /// is 120s. If this ever fails, the constant moved and the measurements in the header need
    /// re-taking before trusting the scaling.</summary>
    [Fact]
    public void TestTimeoutSecForWorker_StaysWellAboveTheSlowestMeasuredTest()
    {
        const double slowestMeasuredSeconds = 4.36; // Tests-SMB, single process, 12 cores
        Assert.True(ParallelFanOut.TestTimeoutSecForWorker(1) > slowestMeasuredSeconds * 10,
            "the single-process budget must leave an order of magnitude over the slowest test "
            + "actually measured, or contention is not the thing being compensated for");
    }

    /// <summary>Positive: when the user has set nothing, the worker environment carries the
    /// shard-scaled value under the exact name TestExecutor.TestTimeout() reads.</summary>
    [Fact]
    public void WorkerEnvironment_ScalesTestTimeoutWhenUserSetNothing()
    {
        var env = ParallelFanOut.WorkerEnvironment(cores: 12, jobs: 6);

        Assert.True(env.TryGetValue("AL_RUNNER_TEST_TIMEOUT_SEC", out var value));
        Assert.Equal((ParallelFanOut.DefaultTestTimeoutSec * 6).ToString(), value);
    }

    /// <summary>Negative: an explicit AL_RUNNER_TEST_TIMEOUT_SEC must win, and must be absent
    /// from the override dictionary entirely — the child inherits the real environment variable
    /// already, exactly as the GC knobs and the emit timeout do.</summary>
    [Fact]
    public void WorkerEnvironment_DefersToAnExplicitTestTimeout()
    {
        var env = ParallelFanOut.WorkerEnvironment(cores: 12, jobs: 6, userTestTimeoutSec: "30");

        Assert.False(env.ContainsKey("AL_RUNNER_TEST_TIMEOUT_SEC"),
            "a user-set AL_RUNNER_TEST_TIMEOUT_SEC must not be overridden by the scaled default");
    }

    /// <summary>Setting one knob must not suppress the others. The emit timeout and the test
    /// timeout are independent: someone who pinned the emit budget for a large bundle has said
    /// nothing about how long a single test may run.</summary>
    [Fact]
    public void WorkerEnvironment_ExplicitEmitTimeoutDoesNotSuppressTestTimeoutScaling()
    {
        var env = ParallelFanOut.WorkerEnvironment(cores: 12, jobs: 4, userEmitTimeoutSec: "900");

        Assert.False(env.ContainsKey("AL_RUNNER_EMIT_TIMEOUT_SEC"));
        Assert.Equal((ParallelFanOut.DefaultTestTimeoutSec * 4).ToString(),
            env["AL_RUNNER_TEST_TIMEOUT_SEC"]);
    }

    /// <summary>And the reverse, so neither knob can start shadowing the other unnoticed.</summary>
    [Fact]
    public void WorkerEnvironment_ExplicitTestTimeoutDoesNotSuppressEmitTimeoutScaling()
    {
        var env = ParallelFanOut.WorkerEnvironment(cores: 12, jobs: 4, userTestTimeoutSec: "30");

        Assert.False(env.ContainsKey("AL_RUNNER_TEST_TIMEOUT_SEC"));
        Assert.Equal((ParallelFanOut.DefaultEmitTimeoutSec * 4).ToString(),
            env["AL_RUNNER_EMIT_TIMEOUT_SEC"]);
    }

    /// <summary>The scaled default is an ENV var, and TestExecutor.TestTimeout() ranks an
    /// explicit --test-timeout above it. That ordering is what keeps tests which pass a
    /// deliberate short timeout — SuiteAbortOnTimeoutTests drives the watchdog with
    /// --test-timeout 2 — unaffected by this change: the flag is in ValueTakingFlags, so it
    /// reaches the worker as an argument and wins over anything set here.</summary>
    [Fact]
    public void ExplicitTestTimeoutFlag_ReachesTheWorker_AndOutranksTheScaledEnvDefault()
    {
        Assert.Contains("--test-timeout", ParallelFanOut.ValueTakingFlags);

        var child = ParallelFanOut.BuildChildArgs(
            new[] { "bundleA", "--test-timeout", "2" },
            new[] { "bundleA" },
            new[] { "bundleA" },
            "/tmp/shard-0.xml");

        var i = child.IndexOf("--test-timeout");
        Assert.True(i >= 0, "--test-timeout must survive into the worker's argv");
        Assert.Equal("2", child[i + 1]);
    }
}
