using AlRunner;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Regression tests for #1220: --init-events must fire BC lifecycle events
/// exactly once per run, not once per codeunit reset.
///
/// Before the fix, Executor.RunTests called FireInitEvent(…) × 4 on every
/// codeunit boundary (and, with method isolation, before every single test).
/// That matched the observed 3.5× perf hit reported in the issue. The fix
/// captures a DB baseline snapshot after the first fire and restores it on
/// subsequent resets — subscribers fire exactly 4 times per run regardless
/// of how many test codeunits or test methods the suite contains.
/// </summary>
[Collection("Pipeline")]
public class InitEventsBaselineTests
{
    private static readonly string RepoRoot = System.IO.Path.GetFullPath(
        System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string TestPath(string testCase, string sub)
    {
        // 42-init-events-fires-once lives under tests/init-events/, not under a bucket.
        var candidate = System.IO.Path.Combine(RepoRoot, "tests", "init-events", testCase, sub);
        if (System.IO.Directory.Exists(candidate))
            return candidate;
        return System.IO.Path.Combine(CliRunner.FindTestCase(testCase), sub);
    }

    [Fact]
    public void InitEvents_FireExactlyFourTimes_AcrossMultipleCodeunits_CodeunitIsolation()
    {
        // Fixture has two test codeunits (57301, 57302). Under codeunit isolation
        // there are two codeunit-level resets (one per codeunit). Pre-fix each
        // reset fired 4 events → 8 total. With the fix init fires once → 4 total.
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            TestIsolation = AlRunner.TestIsolation.Codeunit,
            InitEvents = true,
            InputPaths =
            {
                TestPath("42-init-events-fires-once", "src"),
                TestPath("42-init-events-fires-once", "test")
            }
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(4, Executor.InitEventFireCount);
    }

    [Fact]
    public void InitEvents_FireExactlyFourTimes_AcrossMultipleTests_MethodIsolation()
    {
        // With method isolation, every test method triggers a reset. The fixture
        // has 4 test methods across 2 codeunits. Pre-fix that was 16 subscriber
        // fires; after the fix it stays at 4.
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            TestIsolation = AlRunner.TestIsolation.Method,
            InitEvents = true,
            InputPaths =
            {
                TestPath("42-init-events-fires-once", "src"),
                TestPath("42-init-events-fires-once", "test")
            }
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(4, Executor.InitEventFireCount);
    }

    [Fact]
    public void InitEvents_NotSet_FireCountIsZero()
    {
        // Sanity: without --init-events, the counter stays at 0 regardless of how
        // many resets happen. Uses the existing 40-init-events fixture which does
        // not rely on init-events firing to pass its happy-path assertion; we only
        // check the counter here (the fixture's test itself will fail without the
        // flag, so we assert non-zero exit to prove the fixture is wired normally).
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            TestIsolation = AlRunner.TestIsolation.Codeunit,
            InitEvents = false,
            InputPaths =
            {
                TestPath("40-init-events", "src"),
                TestPath("40-init-events", "test")
            }
        });

        Assert.Equal(0, Executor.InitEventFireCount);
        // Without --init-events the 40-init-events suite's own assertion fails:
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public void InitEvents_ExistingIdempotentFixtureStillPasses()
    {
        // Regression guard: the always-insert subscriber fixture must still work
        // under the new snapshot-restore semantics.
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            TestIsolation = AlRunner.TestIsolation.Codeunit,
            InitEvents = true,
            InputPaths =
            {
                TestPath("41-init-events-idempotent", "src"),
                TestPath("41-init-events-idempotent", "test")
            }
        });

        Assert.Equal(0, result.ExitCode);
    }
}
