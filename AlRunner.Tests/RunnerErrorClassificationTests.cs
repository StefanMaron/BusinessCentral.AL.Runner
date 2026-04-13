using System.Text.Json;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Tests for runner-error classification: distinguishing user logic failures (FAIL)
/// from runner limitations (ERROR + IsRunnerBug=true).
///
/// Full end-to-end coverage of IsRunnerBug=true requires a package-based scenario
/// (codeunit available as symbol reference but not compiled into the assembly).
/// The detection component is verified here via direct MockCodeunitHandle invocation.
/// </summary>
[Collection("Pipeline")]
public class RunnerErrorClassificationTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string TestPath(string testCase, string sub) =>
        Path.Combine(RepoRoot, "tests", testCase, sub);

    /// <summary>
    /// Verifies the detection component: MockCodeunitHandle throws the specific
    /// InvalidOperationException that Executor.IsRunnerError() recognises, with
    /// the right message and "AlRunner.Runtime.Mock" in the stack trace.
    /// </summary>
    [Fact]
    public void MockCodeunitHandle_UnknownId_ThrowsClassifiableException()
    {
        var handle = new AlRunner.Runtime.MockCodeunitHandle(99999);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            handle.Invoke(0, Array.Empty<object>()));

        Assert.Contains("99999", ex.Message);
        Assert.Contains("not found in assembly", ex.Message);
        // IsRunnerError() checks for this frame — must be present.
        Assert.Contains("AlRunner.Runtime.Mock", ex.StackTrace ?? "");
    }

    /// <summary>
    /// Regular assertion failures (user logic wrong) must NOT be classified as
    /// runner bugs. IsRunnerBug must stay false for plain Fail outcomes.
    /// </summary>
    [Fact]
    public void PipelineResult_AssertFail_IsRunnerBugFalse()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("06-intentional-failure", "src"), TestPath("06-intentional-failure", "test") }
        });

        Assert.NotEqual(0, result.ExitCode);
        var failedTests = result.Tests.Where(t => t.Status == TestStatus.Fail).ToList();
        Assert.NotEmpty(failedTests);
        Assert.All(failedTests, t => Assert.False(t.IsRunnerBug,
            $"'{t.Name}' is a user logic failure — IsRunnerBug must be false"));
    }

    /// <summary>
    /// Passing tests must have IsRunnerBug = false (the zero value / default).
    /// </summary>
    [Fact]
    public void PipelineResult_TestPass_IsRunnerBugFalse()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("01-pure-function", "src"), TestPath("01-pure-function", "test") }
        });

        Assert.Equal(0, result.ExitCode);
        Assert.All(result.Tests.Where(t => t.Status == TestStatus.Pass),
            t => Assert.False(t.IsRunnerBug,
                $"'{t.Name}' passed — IsRunnerBug must not be set"));
    }

    /// <summary>
    /// JSON output must include isRunnerBug:true for runner-limitation errors.
    /// Verified via SerializeJsonOutput directly since triggering IsRunnerBug=true
    /// end-to-end requires a package-based scenario.
    /// </summary>
    [Fact]
    public void JsonOutput_IsRunnerBugTrue_AppearsInJson()
    {
        var tests = new List<TestResult>
        {
            new() { Name = "TestRunnerBug",   Status = TestStatus.Error, IsRunnerBug = true,  Message = "Codeunit 99999 not found in assembly" },
            new() { Name = "TestUserFail",    Status = TestStatus.Fail,  IsRunnerBug = false, Message = "Expected 1, got 2" },
            new() { Name = "TestPass",        Status = TestStatus.Pass }
        };

        var json = AlRunnerPipeline.SerializeJsonOutput(tests, exitCode: 1, indented: false);
        using var doc = JsonDocument.Parse(json);
        var testsArr = doc.RootElement.GetProperty("tests").EnumerateArray().ToList();

        // Runner-bug error: isRunnerBug must be true
        var runnerBugTest = testsArr.First(t => t.GetProperty("name").GetString() == "TestRunnerBug");
        Assert.Equal("error", runnerBugTest.GetProperty("status").GetString());
        Assert.True(runnerBugTest.GetProperty("isRunnerBug").GetBoolean());

        // User assertion failure: isRunnerBug must be absent (null → omitted)
        var userFailTest = testsArr.First(t => t.GetProperty("name").GetString() == "TestUserFail");
        Assert.Equal("fail", userFailTest.GetProperty("status").GetString());
        Assert.False(userFailTest.TryGetProperty("isRunnerBug", out _),
            "isRunnerBug should be omitted (null) when false to keep JSON clean");

        // Pass: isRunnerBug must be absent
        var passTest = testsArr.First(t => t.GetProperty("name").GetString() == "TestPass");
        Assert.Equal("pass", passTest.GetProperty("status").GetString());
        Assert.False(passTest.TryGetProperty("isRunnerBug", out _));
    }

    /// <summary>
    /// Output for runner-bug errors must contain the runner-limitation hint with
    /// the GitHub issues link. Verified via PrintResults on a synthetic result set.
    /// </summary>
    [Fact]
    public void PrintResults_IsRunnerBugTrue_OutputContainsHint()
    {
        var tests = new List<TestResult>
        {
            new() { Name = "TestMissing", Status = TestStatus.Error, IsRunnerBug = true, Message = "Codeunit 99999 not found in assembly" }
        };

        var captured = new System.IO.StringWriter();
        var prev = Console.Out;
        Console.SetOut(captured);
        try { Executor.PrintResults(tests); }
        finally { Console.SetOut(prev); }

        var output = captured.ToString();
        Assert.Contains("ERROR", output);
        Assert.Contains("Runner limitation", output);
        Assert.Contains("github.com/StefanMaron/BusinessCentral.AL.Runner/issues", output);
    }
}
