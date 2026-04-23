using AlRunner;
using Xunit;

namespace AlRunner.Tests;

[Collection("Pipeline")]
public class MissingDepHintTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string TestPath(string testCase, string sub) =>
        Path.Combine(CliRunner.FindTestCase(testCase), sub);

    [Fact]
    public void NamespaceMismatch_EmitsHintPointingAtFoundNamespace()
    {
        // Stub declares `codeunit 10 "Type Helper"` under System.Utilities;
        // consumer imports System.Reflection and references Type Helper.
        // Runner should surface both the missing-dependency error AND a
        // namespace-mismatch hint that names the stub file + declared namespace.
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            TestIsolation = AlRunner.TestIsolation.Method,
            StubPaths = { TestPath("46-missing-dep-hint", "stubs") },
            InputPaths = { TestPath("46-missing-dep-hint", "src") }
        });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Missing dependencies:", result.StdErr);
        Assert.Contains("Type Helper", result.StdErr);
        Assert.Contains("hint:", result.StdErr);
        Assert.Contains("System.Utilities", result.StdErr);
        Assert.Contains("TypeHelper.al", result.StdErr);
    }

    [Fact]
    public void CorrectNamespace_NoHintNeeded()
    {
        // Sanity: when no stubs are loaded, the code path still works and
        // produces a plain missing-dependency message without the hint.
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            TestIsolation = AlRunner.TestIsolation.Method,
            InputPaths = { TestPath("46-missing-dep-hint", "src") }
        });

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("hint:", result.StdErr);
    }
}
