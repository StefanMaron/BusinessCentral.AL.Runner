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

    [Fact]
    public void PrintResults_SummaryLine_AllPass_ShowsCompactFormat()
    {
        var tests = new List<TestResult>
        {
            new() { Name = "TestA", Status = TestStatus.Pass, DurationMs = 5 },
            new() { Name = "TestB", Status = TestStatus.Pass, DurationMs = 3 },
        };

        var captured = new System.IO.StringWriter();
        var prev = Console.Out;
        Console.SetOut(captured);
        try { Executor.PrintResults(tests, totalMs: 100); }
        finally { Console.SetOut(prev); }

        var output = captured.ToString();
        Assert.Matches(@"2 passed in 0\.1s", output);
        Assert.DoesNotContain("failed", output);
        Assert.DoesNotContain("blocked", output);
    }

    [Fact]
    public void PrintResults_SummaryLine_WithBlockedTests_ShowsBlockedFormat()
    {
        var tests = new List<TestResult>
        {
            new() { Name = "TestPass", Status = TestStatus.Pass },
            new() { Name = "TestBlocked", Status = TestStatus.Error, IsRunnerBug = true, Message = "Not supported" },
        };

        var captured = new System.IO.StringWriter();
        var prev = Console.Out;
        Console.SetOut(captured);
        try { Executor.PrintResults(tests, totalMs: 2500); }
        finally { Console.SetOut(prev); }

        var output = captured.ToString();
        Assert.Contains("1 passed, 1 blocked (runner limitation) in 2.5s", output);
    }

    [Fact]
    public void PrintResults_SummaryLine_WithFailures_ShowsFullFormat()
    {
        var tests = new List<TestResult>
        {
            new() { Name = "TestPass", Status = TestStatus.Pass },
            new() { Name = "TestFail", Status = TestStatus.Fail, Message = "assertion failed" },
            new() { Name = "TestBlocked", Status = TestStatus.Error, IsRunnerBug = true },
        };

        var captured = new System.IO.StringWriter();
        var prev = Console.Out;
        Console.SetOut(captured);
        try { Executor.PrintResults(tests, totalMs: 1800); }
        finally { Console.SetOut(prev); }

        var output = captured.ToString();
        Assert.Contains("1 passed, 1 failed, 1 blocked (runner limitation) in 1.8s", output);
    }

    [Fact]
    public void PrintResults_SummaryLine_NoElapsedTime_OmitsTimeClause()
    {
        var tests = new List<TestResult>
        {
            new() { Name = "TestA", Status = TestStatus.Pass },
        };

        var captured = new System.IO.StringWriter();
        var prev = Console.Out;
        Console.SetOut(captured);
        try { Executor.PrintResults(tests); }
        finally { Console.SetOut(prev); }

        var output = captured.ToString();
        Assert.Contains("1 passed", output);
        Assert.DoesNotContain(" in ", output);
    }

    // ---------------------------------------------------------------------------
    // ExitCode() differentiation tests
    // ---------------------------------------------------------------------------

    /// <summary>All tests pass → exit 0.</summary>
    [Fact]
    public void ExitCode_AllPass_Returns0()
    {
        var results = new List<TestResult>
        {
            new() { Name = "T1", Status = TestStatus.Pass },
            new() { Name = "T2", Status = TestStatus.Pass }
        };
        Assert.Equal(0, Executor.ExitCode(results));
    }

    /// <summary>Any Status=Fail → exit 1 (real assertion failure).</summary>
    [Fact]
    public void ExitCode_AssertionFailure_Returns1()
    {
        var results = new List<TestResult>
        {
            new() { Name = "T1", Status = TestStatus.Pass },
            new() { Name = "T2", Status = TestStatus.Fail, Message = "Expected 1, got 2" }
        };
        Assert.Equal(1, Executor.ExitCode(results));
    }

    /// <summary>Only runner errors (no failures) → exit 2.</summary>
    [Fact]
    public void ExitCode_OnlyRunnerErrors_Returns2()
    {
        var results = new List<TestResult>
        {
            new() { Name = "T1", Status = TestStatus.Pass },
            new() { Name = "T2", Status = TestStatus.Error, IsRunnerBug = true, Message = "Codeunit 99 not found" }
        };
        Assert.Equal(2, Executor.ExitCode(results));
    }

    /// <summary>Any Fail trumps runner errors — mixed results → exit 1.</summary>
    [Fact]
    public void ExitCode_FailAndError_Returns1()
    {
        var results = new List<TestResult>
        {
            new() { Name = "T1", Status = TestStatus.Fail,  Message = "assertion" },
            new() { Name = "T2", Status = TestStatus.Error, IsRunnerBug = true, Message = "not found" }
        };
        Assert.Equal(1, Executor.ExitCode(results));
    }

    /// <summary>Empty result list → exit 1 (nothing ran, treat as error).</summary>
    [Fact]
    public void ExitCode_EmptyResults_Returns1()
    {
        Assert.Equal(1, Executor.ExitCode(new List<TestResult>()));
    }

    /// <summary>
    /// Pipeline with a real assertion failure returns exit 1.
    /// </summary>
    [Fact]
    public void Pipeline_AssertionFailure_Returns1()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("06-intentional-failure", "src"), TestPath("06-intentional-failure", "test") }
        });

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(result.Tests, t => t.Status == TestStatus.Fail);
    }

    // ---------------------------------------------------------------------------
    // PrintResults deduplication tests (issue #70)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// When multiple tests share the same error message, the message should appear
    /// exactly once in the output (as a WARN block), not repeated per test.
    /// Each individual ERROR line should be compact — just the test name + "(blocked)".
    /// </summary>
    [Fact]
    public void PrintResults_RepeatedErrorMessage_MessageShownOnceAsWarn()
    {
        var sharedMessage = "CompilationExcludedException: Codeunit 50123 was excluded during compilation.";
        var tests = new List<TestResult>
        {
            new() { Name = "Test1", Status = TestStatus.Error, Message = sharedMessage },
            new() { Name = "Test2", Status = TestStatus.Error, Message = sharedMessage },
            new() { Name = "Test3", Status = TestStatus.Error, Message = sharedMessage },
        };

        var output = CaptureStdOut(() => Executor.PrintResults(tests));

        // Message should appear exactly once (in the WARN block)
        var msgOccurrences = CountOccurrences(output, sharedMessage);
        Assert.True(msgOccurrences == 1, $"Expected message to appear exactly once, but found {msgOccurrences} occurrence(s):\n{output}");

        // Should have a WARN prefix for the summary
        Assert.Contains("WARN", output);

        // Each test should still have an ERROR line
        Assert.Contains("ERROR Test1", output);
        Assert.Contains("ERROR Test2", output);
        Assert.Contains("ERROR Test3", output);

        // The individual ERROR lines should be compact (contain "blocked")
        Assert.Contains("(blocked)", output);
    }

    /// <summary>
    /// With verbose=true, full details are printed per test (old behavior, no deduplication).
    /// </summary>
    [Fact]
    public void PrintResults_RepeatedErrorMessage_Verbose_ShowsFullDetailsPerTest()
    {
        var sharedMessage = "CompilationExcludedException: Codeunit 50123 was excluded during compilation.";
        var tests = new List<TestResult>
        {
            new() { Name = "Test1", Status = TestStatus.Error, Message = sharedMessage },
            new() { Name = "Test2", Status = TestStatus.Error, Message = sharedMessage },
        };

        var output = CaptureStdOut(() => Executor.PrintResults(tests, verbose: true));

        // With verbose, message appears once per test (not deduplicated)
        var msgOccurrences = CountOccurrences(output, sharedMessage);
        Assert.True(msgOccurrences == 2, $"Expected message to appear once per test (2 times) in verbose mode, but found {msgOccurrences}:\n{output}");

        // Should NOT have WARN prefix in verbose mode
        Assert.DoesNotContain("WARN", output);
    }

    /// <summary>
    /// A single test with a unique error message should still print full details
    /// (no WARN block, no compact line — unchanged behavior).
    /// </summary>
    [Fact]
    public void PrintResults_UniqueErrorMessage_FullDetailsShown()
    {
        var uniqueMessage = "NotSupportedException: Page 50100 is not supported.";
        var tests = new List<TestResult>
        {
            new() { Name = "TestA", Status = TestStatus.Error, Message = uniqueMessage, IsRunnerBug = true },
        };

        var output = CaptureStdOut(() => Executor.PrintResults(tests));

        // Message should appear in output
        Assert.Contains(uniqueMessage, output);
        Assert.Contains("ERROR TestA", output);

        // Should NOT have WARN prefix (no deduplication for unique errors)
        Assert.DoesNotContain("WARN", output);

        // ERROR line should NOT be compact — no "(blocked)" suffix when unique
        Assert.DoesNotContain("(blocked)", output);
    }

    /// <summary>
    /// Tests with different messages should each still show their own full details.
    /// No WARN block should be shown.
    /// </summary>
    [Fact]
    public void PrintResults_DifferentErrorMessages_NoDeduplication()
    {
        var tests = new List<TestResult>
        {
            new() { Name = "Test1", Status = TestStatus.Error, Message = "Error message A", IsRunnerBug = true },
            new() { Name = "Test2", Status = TestStatus.Error, Message = "Error message B", IsRunnerBug = true },
        };

        var output = CaptureStdOut(() => Executor.PrintResults(tests));

        Assert.Contains("Error message A", output);
        Assert.Contains("Error message B", output);
        Assert.DoesNotContain("WARN", output);
        Assert.DoesNotContain("(blocked)", output);
    }

    /// <summary>
    /// FAIL tests should never be deduplicated — each failure is unique and must
    /// always show its full message.
    /// </summary>
    [Fact]
    public void PrintResults_RepeatedFailMessage_NeverDeduplicated()
    {
        var sharedMessage = "Expected 1, got 2";
        var tests = new List<TestResult>
        {
            new() { Name = "Test1", Status = TestStatus.Fail, Message = sharedMessage },
            new() { Name = "Test2", Status = TestStatus.Fail, Message = sharedMessage },
        };

        var output = CaptureStdOut(() => Executor.PrintResults(tests));

        // Each FAIL should show its message (2 occurrences expected)
        var msgOccurrences = CountOccurrences(output, sharedMessage);
        Assert.True(msgOccurrences == 2, $"Expected fail message to appear once per test, got {msgOccurrences}:\n{output}");

        // Should NOT have WARN prefix (no deduplication for failures)
        Assert.DoesNotContain("WARN", output);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static string CaptureStdOut(Action action)
    {
        var captured = new System.IO.StringWriter();
        var prev = Console.Out;
        Console.SetOut(captured);
        try { action(); }
        finally { Console.SetOut(prev); }
        return captured.ToString();
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }
}
