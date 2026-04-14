using System.Text.Json;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

[Collection("Pipeline")]
public class CaptureValuesTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string TestPath(string testCase, string sub) =>
        Path.Combine(CliRunner.FindTestCase(testCase), sub);

    [Fact]
    public void CaptureValues_ReturnsVariableSnapshots()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("01-pure-function", "src"), TestPath("01-pure-function", "test") },
            CaptureValues = true
        });

        Assert.Equal(0, result.ExitCode);
        // Should have captured some variable values
        Assert.NotEmpty(result.CapturedValues);
    }

    [Fact]
    public void CaptureValues_IncludesVariableNameAndValue()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("01-pure-function", "src"), TestPath("01-pure-function", "test") },
            CaptureValues = true
        });

        Assert.Equal(0, result.ExitCode);
        var capture = result.CapturedValues.First();
        Assert.NotEmpty(capture.ScopeName);
        Assert.NotEmpty(capture.VariableName);
        // Value can be null for uninitialized variables, but the property must exist
    }

    [Fact]
    public void CaptureValues_Disabled_NoCapturedValues()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("01-pure-function", "src"), TestPath("01-pure-function", "test") },
            CaptureValues = false
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.CapturedValues);
    }

    [Fact]
    public void CaptureValues_OutputJson_IncludesCaptures()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("01-pure-function", "src"), TestPath("01-pure-function", "test") },
            CaptureValues = true,
            OutputJson = true
        });

        var doc = JsonDocument.Parse(result.StdOut.Trim());
        Assert.True(doc.RootElement.TryGetProperty("capturedValues", out var captures));
        Assert.True(captures.GetArrayLength() > 0);
    }
}
