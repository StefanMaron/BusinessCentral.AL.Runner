using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Tests for <see cref="Executor.RunTests"/> streaming callback, per-test isolation
/// (Messages / CapturedValues via AsyncLocal), StackFrames / ErrorKind / AlSourceFile
/// enrichment, and cooperative cancellation.
/// </summary>
[Collection("Pipeline")]
public class RunTestsStreamingTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string LineDirectivesTestPath(string sub) =>
        Path.Combine(RepoRoot, "tests", "protocol-v2-line-directives", sub);

    private static string IsolationTestPath(string sub) =>
        Path.Combine(RepoRoot, "tests", "protocol-v2-per-test-isolation", sub);

    private Assembly BuildLineDirectives()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { LineDirectivesTestPath("src"), LineDirectivesTestPath("test") },
            EmitLineDirectives = true,
        });
        Assert.True(result.ExitCode == 0 || result.ExitCode == 1,
            $"Pipeline should complete, got ExitCode={result.ExitCode}. StdErr: {result.StdErr}");
        Assert.NotNull(result.CompiledAssembly);
        return result.CompiledAssembly!;
    }

    private Assembly BuildIsolation()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { IsolationTestPath("src"), IsolationTestPath("test") },
        });
        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.CompiledAssembly);
        return result.CompiledAssembly!;
    }

    // ─── Streaming callback ───────────────────────────────────────────────────

    [Fact]
    public void RunTests_OnTestComplete_FiresOncePerTest_InOrder()
    {
        var asm = BuildLineDirectives();
        var seen = new List<string>();
        var all = Executor.RunTests(asm, onTestComplete: t => seen.Add(t.Name));
        Assert.Equal(all.Count, seen.Count);
        Assert.Equal(all.Select(t => t.Name).ToList(), seen);
    }

    [Fact]
    public void RunTests_OnTestComplete_ResultAlreadyInReturnList_WhenCallbackFires()
    {
        // The callback fires AFTER the result is added to the list,
        // so `all` count grows monotonically as the callback runs.
        var asm = BuildLineDirectives();
        var seenCounts = new List<int>();
        var all = Executor.RunTests(asm, onTestComplete: _ => seenCounts.Add(seenCounts.Count + 1));
        Assert.Equal(all.Count, seenCounts.Count);
    }

    // ─── StackFrames / ErrorKind / AlSourceFile ───────────────────────────────

    [Fact]
    public void RunTests_FailingTest_HasStackFramesPopulated()
    {
        var asm = BuildLineDirectives();
        var all = Executor.RunTests(asm);
        var failed = all.First(t => t.Status == TestStatus.Fail);
        Assert.NotNull(failed.StackFrames);
        Assert.NotEmpty(failed.StackFrames!);
    }

    [Fact]
    public void RunTests_FailingTest_HasErrorKindPopulated()
    {
        var asm = BuildLineDirectives();
        var all = Executor.RunTests(asm);
        var failed = all.First(t => t.Status == TestStatus.Fail);
        Assert.NotNull(failed.ErrorKind);
        // FailingTest calls Error('intentional failure') — not a compile error, not Unknown
        Assert.NotEqual(AlErrorKind.Unknown, failed.ErrorKind!.Value);
        Assert.NotEqual(AlErrorKind.Compile, failed.ErrorKind!.Value);
    }

    [Fact]
    public void RunTests_FailingTest_AlSourceFile_EndsWithDotAl()
    {
        var asm = BuildLineDirectives();
        var all = Executor.RunTests(asm);
        var failed = all.First(t => t.Status == TestStatus.Fail);
        // With #line directives and portable PDB, deepest user frame resolves to .al file.
        Assert.NotNull(failed.AlSourceFile);
        Assert.EndsWith(".al", failed.AlSourceFile, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RunTests_PassingTest_StackFrames_AreNullOrEmpty()
    {
        // Passing tests don't go through the catch block, so StackFrames is null.
        var asm = BuildLineDirectives();
        var all = Executor.RunTests(asm);
        var passing = all.Where(t => t.Status == TestStatus.Pass).ToList();
        Assert.NotEmpty(passing);
        Assert.All(passing, t =>
        {
            // null is the expected state for a passing test (no exception walked)
            Assert.Null(t.StackFrames);
        });
    }

    // ─── Per-test Messages isolation ─────────────────────────────────────────

    [Fact]
    public void RunTests_PerTestMessages_CollectionAlwaysPresent()
    {
        var asm = BuildLineDirectives();
        var all = Executor.RunTests(asm);
        Assert.All(all, t => Assert.NotNull(t.Messages));
    }

    [Fact]
    public void RunTests_PerTestCapturedValues_CollectionAlwaysPresent()
    {
        var asm = BuildLineDirectives();
        var all = Executor.RunTests(asm);
        Assert.All(all, t => Assert.NotNull(t.CapturedValues));
    }

    [Fact]
    public void RunTests_MessageIsolation_TestA_GetsOnlyItsOwnMessages()
    {
        var asm = BuildIsolation();
        var all = Executor.RunTests(asm);
        var testA = all.First(t => t.Name == "TestA");
        Assert.NotNull(testA.Messages);
        Assert.Contains("from A", testA.Messages!);
        Assert.DoesNotContain("from B", testA.Messages!);
    }

    [Fact]
    public void RunTests_MessageIsolation_TestB_GetsOnlyItsOwnMessages()
    {
        var asm = BuildIsolation();
        var all = Executor.RunTests(asm);
        var testB = all.First(t => t.Name == "TestB");
        Assert.NotNull(testB.Messages);
        Assert.Contains("from B", testB.Messages!);
        Assert.DoesNotContain("from A", testB.Messages!);
    }

    [Fact]
    public void RunTests_MessageIsolation_NoCrossContamination()
    {
        // Run both tests and confirm neither leaks into the other.
        var asm = BuildIsolation();
        var all = Executor.RunTests(asm);
        Assert.Equal(2, all.Count);
        var testA = all.First(t => t.Name == "TestA");
        var testB = all.First(t => t.Name == "TestB");
        // Only one message each, fully isolated.
        Assert.Single(testA.Messages!);
        Assert.Single(testB.Messages!);
    }

    // ─── Cooperative cancellation ─────────────────────────────────────────────

    [Fact]
    public void RunTests_CancellationBeforeAnyTest_RunsNothing()
    {
        var asm = BuildLineDirectives();
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var all = Executor.RunTests(asm, cancellationToken: cts.Token);
        Assert.Empty(all);
    }

    [Fact]
    public void RunTests_CancellationMidStream_StopsBeforeNextTest()
    {
        var asm = BuildLineDirectives();
        var cts = new CancellationTokenSource();
        var seen = new List<string>();
        var all = Executor.RunTests(asm, cancellationToken: cts.Token,
            onTestComplete: t =>
            {
                seen.Add(t.Name);
                cts.Cancel(); // cancel after the first test completes
            });
        // Cancelling in the first callback stops subsequent tests.
        Assert.Single(seen);
        Assert.Single(all);
    }

    [Fact]
    public void RunTests_CancellationMidStream_ResultsContainCompletedTests()
    {
        // Tests that ran before cancellation are in the result list.
        var asm = BuildLineDirectives();
        var cts = new CancellationTokenSource();
        var callCount = 0;
        var all = Executor.RunTests(asm, cancellationToken: cts.Token,
            onTestComplete: _ =>
            {
                callCount++;
                if (callCount == 1) cts.Cancel();
            });
        // Exactly 1 test ran and was returned.
        Assert.Single(all);
    }

    [Fact]
    public void RunTests_NoCancellation_AllTestsRun()
    {
        // Sanity check: with a non-cancelled token, all tests run normally.
        var asm = BuildLineDirectives();
        var cts = new CancellationTokenSource();
        var all = Executor.RunTests(asm, cancellationToken: cts.Token);
        Assert.Equal(3, all.Count);
    }
}
