using System.Text.Json;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

[Collection("Pipeline")]
public class ErrorLineMappingTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string TestPath(string testCase, string sub) =>
        Path.Combine(CliRunner.FindTestCase(testCase), sub);

    [Fact]
    public void FailingTest_IncludesAlSourceLine()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("06-intentional-failure", "src"), TestPath("06-intentional-failure", "test") }
        });

        Assert.NotEqual(0, result.ExitCode);
        var failedTest = result.Tests.First(t => t.Status == TestStatus.Fail);
        // Should have AL source line info
        Assert.NotNull(failedTest.AlSourceLine);
        Assert.True(failedTest.AlSourceLine > 0, "AL source line should be positive");
    }

    [Fact]
    public void PassingTest_NoAlSourceLine()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("01-pure-function", "src"), TestPath("01-pure-function", "test") }
        });

        Assert.Equal(0, result.ExitCode);
        // Passing tests should not have error line info
        Assert.All(result.Tests, t => Assert.Null(t.AlSourceLine));
    }

    [Fact]
    public void OutputJson_FailingTest_IncludesAlLine()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("06-intentional-failure", "src"), TestPath("06-intentional-failure", "test") },
            OutputJson = true
        });

        var doc = JsonDocument.Parse(result.StdOut.Trim());
        var tests = doc.RootElement.GetProperty("tests");
        var failedTest = tests.EnumerateArray().First(t => t.GetProperty("status").GetString() == "fail");
        Assert.True(failedTest.TryGetProperty("alSourceLine", out var line));
        Assert.True(line.GetInt32() > 0);
    }

    [Fact]
    public void FailingTest_IncludesAlSourceColumn()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("06-intentional-failure", "src"), TestPath("06-intentional-failure", "test") }
        });

        Assert.NotEqual(0, result.ExitCode);
        var failedTest = result.Tests.First(t => t.Status == TestStatus.Fail);
        // Column should be populated alongside the line so IDE decorations
        // can underline the exact failing expression, not the whole row.
        Assert.NotNull(failedTest.AlSourceColumn);
        Assert.True(failedTest.AlSourceColumn > 0, "AL source column should be positive");
    }

    [Fact]
    public void OutputJson_FailingTest_IncludesAlColumn()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("06-intentional-failure", "src"), TestPath("06-intentional-failure", "test") },
            OutputJson = true
        });

        var doc = JsonDocument.Parse(result.StdOut.Trim());
        var tests = doc.RootElement.GetProperty("tests");
        var failedTest = tests.EnumerateArray().First(t => t.GetProperty("status").GetString() == "fail");
        Assert.True(failedTest.TryGetProperty("alSourceColumn", out var col));
        Assert.True(col.GetInt32() > 0);
    }
}
