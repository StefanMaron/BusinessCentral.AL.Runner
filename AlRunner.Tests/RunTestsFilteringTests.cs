using System.Collections.Generic;
using System.Reflection;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Tests for <see cref="Executor.RunTests"/> filtering via <see cref="TestFilter"/>.
/// Drives the executor directly with a compiled assembly so we can pass custom filters
/// without going through the full pipeline options.
/// </summary>
[Collection("Pipeline")]
public class RunTestsFilteringTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string TestPath(string sub) =>
        Path.Combine(RepoRoot, "tests", "protocol-v2-line-directives", sub);

    private Assembly Build()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("src"), TestPath("test") },
            EmitLineDirectives = true,
        });
        // ExitCode may be 1 because FailingTest intentionally fails.
        // A compile error would be ExitCode 2+.
        Assert.True(result.ExitCode == 0 || result.ExitCode == 1,
            $"Pipeline should complete, got ExitCode={result.ExitCode}. StdErr: {result.StdErr}");
        Assert.NotNull(result.CompiledAssembly);
        return result.CompiledAssembly!;
    }

    [Fact]
    public void RunTests_NoFilter_RunsAll()
    {
        var asm = Build();
        var all = Executor.RunTests(asm);
        // The fixture has 3 tests: ComputeDoubles, FailingTest, ConditionalBranchExercises.
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public void RunTests_WithProcFilter_RunsOnlyMatching()
    {
        var asm = Build();
        var filter = new TestFilter(null, new HashSet<string> { "ComputeDoubles" });
        var filtered = Executor.RunTests(asm, filter: filter);
        Assert.Single(filtered);
        Assert.Equal("ComputeDoubles", filtered[0].Name);
    }

    [Fact]
    public void RunTests_WithProcFilter_FailingTest_RunsOnlyMatching()
    {
        var asm = Build();
        var filter = new TestFilter(null, new HashSet<string> { "FailingTest" });
        var filtered = Executor.RunTests(asm, filter: filter);
        Assert.Single(filtered);
        Assert.Equal("FailingTest", filtered[0].Name);
        Assert.Equal(TestStatus.Fail, filtered[0].Status);
    }

    [Fact]
    public void RunTests_WithCodeunitFilter_RunsOnlyMatchingCodeunit()
    {
        var asm = Build();
        var filter = new TestFilter(new HashSet<string> { "CalcTest" }, null);
        var filtered = Executor.RunTests(asm, filter: filter);
        // All 3 tests are in codeunit CalcTest.
        Assert.Equal(3, filtered.Count);
        Assert.All(filtered, r => Assert.Equal("CalcTest", r.CodeunitName));
    }

    [Fact]
    public void RunTests_FilterBothCodeunitAndProc_BothMustMatch()
    {
        var asm = Build();
        var filter = new TestFilter(
            new HashSet<string> { "CalcTest" },
            new HashSet<string> { "FailingTest" });
        var filtered = Executor.RunTests(asm, filter: filter);
        Assert.Single(filtered);
        Assert.Equal("FailingTest", filtered[0].Name);
        Assert.Equal("CalcTest", filtered[0].CodeunitName);
    }

    [Fact]
    public void RunTests_FilterMatchesNothing_ReturnsEmpty()
    {
        var asm = Build();
        var filter = new TestFilter(null, new HashSet<string> { "NoSuchTest" });
        var filtered = Executor.RunTests(asm, filter: filter);
        Assert.Empty(filtered);
    }

    [Fact]
    public void RunTests_FilterWrongCodeunit_ReturnsEmpty()
    {
        var asm = Build();
        var filter = new TestFilter(new HashSet<string> { "NonExistentCodeunit" }, null);
        var filtered = Executor.RunTests(asm, filter: filter);
        Assert.Empty(filtered);
    }

    [Fact]
    public void RunTests_FilterCodeunitMismatch_ProcMatch_ReturnsEmpty()
    {
        // Both must match — wrong codeunit name overrides a correct proc name.
        var asm = Build();
        var filter = new TestFilter(
            new HashSet<string> { "WrongCodeunit" },
            new HashSet<string> { "ComputeDoubles" });
        var filtered = Executor.RunTests(asm, filter: filter);
        Assert.Empty(filtered);
    }

    [Fact]
    public void RunTests_MultipleProcFilter_RunsAllMatching()
    {
        var asm = Build();
        var filter = new TestFilter(null, new HashSet<string> { "ComputeDoubles", "ConditionalBranchExercises" });
        var filtered = Executor.RunTests(asm, filter: filter);
        Assert.Equal(2, filtered.Count);
        Assert.Contains(filtered, r => r.Name == "ComputeDoubles");
        Assert.Contains(filtered, r => r.Name == "ConditionalBranchExercises");
    }
}
