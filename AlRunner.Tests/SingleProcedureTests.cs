using System.Text.Json;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

[Collection("Pipeline")]
public class SingleProcedureTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string TestPath(string testCase, string sub) =>
        Path.Combine(CliRunner.FindTestCase(testCase), sub);

    [Fact]
    public void RunProcedure_ExecutesSingleTest()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("01-pure-function", "src"), TestPath("01-pure-function", "test") },
            RunProcedure = "TestCalculateVAT"
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Single(result.Tests);
        Assert.Equal("TestCalculateVAT", result.Tests[0].Name);
        Assert.Equal(TestStatus.Pass, result.Tests[0].Status);
    }

    [Fact]
    public void RunProcedure_NonexistentProcedure_ReturnsError()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("01-pure-function", "src"), TestPath("01-pure-function", "test") },
            RunProcedure = "NonexistentProcedure"
        });

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("not found", result.StdErr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RunProcedure_FailingTest_ReportsFailure()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("06-intentional-failure", "src"), TestPath("06-intentional-failure", "test") },
            RunProcedure = "TestGreet_WrongExpectedValue"
        });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Single(result.Tests);
        Assert.Equal(TestStatus.Fail, result.Tests[0].Status);
    }
}
