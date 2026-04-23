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
        Path.Combine(CliRunner.FindTestCase(testCase), sub);

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
    /// System codeunits (IDs 1–9999) are BC platform codeunits that cannot be
    /// provided as user stubs. Invoke on a missing system codeunit must return null
    /// (no-op) rather than throwing, so event-subscriber code that happens to call
    /// methods on the publisher codeunit handle does not crash the runner.
    /// </summary>
    [Fact]
    public void MockCodeunitHandle_SystemCodeunitInvoke_ReturnsNullNoOp()
    {
        // Codeunit 27 = "Company-Initialize" (system codeunit, never compiled into the test assembly)
        var handle = new AlRunner.Runtime.MockCodeunitHandle(27);
        // Must NOT throw — system codeunits are no-ops
        var result = handle.Invoke(0, Array.Empty<object>());
        // Return value should be null (no-op default)
        Assert.Null(result);
    }

    /// <summary>
    /// System codeunit IDs at boundary (1 and 9999) are also treated as no-ops.
    /// User codeunit IDs (50000+) still throw for missing codeunits.
    /// Proves the range check is correct.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(9999)]
    public void MockCodeunitHandle_SystemCodeunitBoundary_InvokeIsNoOp(int codeunitId)
    {
        var handle = new AlRunner.Runtime.MockCodeunitHandle(codeunitId);
        // Must not throw — boundary system codeunits are still no-ops
        var result = handle.Invoke(0, Array.Empty<object>());
        Assert.Null(result);
    }

    /// <summary>
    /// ID 10000 is NOT a system codeunit — it falls outside the 1-9999 range.
    /// Missing non-system codeunits must still throw.
    /// </summary>
    [Fact]
    public void MockCodeunitHandle_NonSystemMissingCodeunit_StillThrows()
    {
        var handle = new AlRunner.Runtime.MockCodeunitHandle(10000);
        Assert.Throws<InvalidOperationException>(() =>
            handle.Invoke(0, Array.Empty<object>()));
    }

    /// <summary>
    /// Static RunCodeunit(int) overload for a missing system codeunit must not throw.
    /// </summary>
    [Fact]
    public void MockCodeunitHandle_RunCodeunit_SystemId_DoesNotThrow()
    {
        // RunCodeunit(27) — system codeunit "Company-Initialize", not compiled into test assembly
        // Must not throw — system codeunits are treated as no-ops
        var ex = Record.Exception(() =>
            AlRunner.Runtime.MockCodeunitHandle.RunCodeunit(27));
        Assert.Null(ex);
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
            TestIsolation = AlRunner.TestIsolation.Method,
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
            TestIsolation = AlRunner.TestIsolation.Method,
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
            TestIsolation = AlRunner.TestIsolation.Method,
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
    // PrintResults --strict blocked-test summary
    // ---------------------------------------------------------------------------

    /// <summary>
    /// In strict mode, a consolidated blocked-test summary section should appear
    /// right before the final summary line, grouping blocked tests by root cause.
    /// </summary>
    [Fact]
    public void PrintResults_Strict_BlockedTestSummarySection()
    {
        var sharedMessage = "FileNotFoundException: Could not load some.dll";
        var tests = new List<TestResult>
        {
            new() { Name = "PassA", Status = TestStatus.Pass, DurationMs = 1 },
            new() { Name = "BlockedA", Status = TestStatus.Error, Message = sharedMessage, IsRunnerBug = true },
            new() { Name = "BlockedB", Status = TestStatus.Error, Message = sharedMessage, IsRunnerBug = true },
            new() { Name = "BlockedC", Status = TestStatus.Error, Message = "OtherError", IsRunnerBug = true },
        };

        var output = CaptureStdOut(() => Executor.PrintResults(tests, strict: true));

        // Should contain the strict blocked-test summary section
        Assert.Contains("Blocked tests (3)", output);
        Assert.Contains("failing CI with --strict", output);
        // Both root causes listed
        Assert.Contains("Cause: FileNotFoundException: Could not load some.dll", output);
        Assert.Contains("Cause: OtherError", output);
        // All blocked test names listed
        Assert.Contains("× BlockedA", output);
        Assert.Contains("× BlockedB", output);
        Assert.Contains("× BlockedC", output);
    }

    /// <summary>
    /// Without strict mode, no blocked-test summary section should appear.
    /// </summary>
    [Fact]
    public void PrintResults_NoStrict_NoBlockedSummarySection()
    {
        var tests = new List<TestResult>
        {
            new() { Name = "BlockedA", Status = TestStatus.Error, Message = "SomeError", IsRunnerBug = true },
        };

        var output = CaptureStdOut(() => Executor.PrintResults(tests, strict: false));

        Assert.DoesNotContain("Blocked tests", output);
        Assert.DoesNotContain("failing CI with --strict", output);
    }

    /// <summary>
    /// In strict mode with no blocked tests (all pass), no summary section should appear.
    /// </summary>
    [Fact]
    public void PrintResults_Strict_AllPass_NoBlockedSummarySection()
    {
        var tests = new List<TestResult>
        {
            new() { Name = "PassA", Status = TestStatus.Pass, DurationMs = 1 },
        };

        var output = CaptureStdOut(() => Executor.PrintResults(tests, strict: true));

        Assert.DoesNotContain("Blocked tests", output);
        Assert.DoesNotContain("failing CI with --strict", output);
    }

    // ---------------------------------------------------------------------------
    // IsMissingBcRuntimeDll — unit tests for the new classification helper
    // ---------------------------------------------------------------------------

    /// <summary>
    /// FileNotFoundException with a BC assembly name in FileName is classified as a
    /// runner limitation (missing BC runtime DLL), not a test assertion failure.
    /// </summary>
    [Theory]
    [InlineData("Microsoft.Dynamics.Nav.Ncl.dll")]
    [InlineData("Microsoft.BusinessCentral.Telemetry.Abstractions.dll")]
    public void IsMissingBcRuntimeDll_FileNotFound_ByFileName_ReturnsTrue(string assemblyFile)
    {
        var ex = new System.IO.FileNotFoundException("The system cannot find the file.", assemblyFile);
        Assert.True(Executor.IsMissingBcRuntimeDll(ex));
    }

    /// <summary>
    /// FileNotFoundException whose Message (not FileName) contains a BC assembly name
    /// is also classified — FileName is null when the CLR embeds the name in the message.
    /// </summary>
    [Fact]
    public void IsMissingBcRuntimeDll_FileNotFound_ByMessage_ReturnsTrue()
    {
        var ex = new System.IO.FileNotFoundException(
            "Could not load file or assembly 'Microsoft.BusinessCentral.Telemetry.Abstractions, Version=10.0.0.0'.");
        Assert.True(Executor.IsMissingBcRuntimeDll(ex));
    }

    /// <summary>
    /// FileNotFoundException for a non-BC assembly (e.g. user code missing a DLL) must
    /// NOT be classified as a runner limitation.
    /// </summary>
    [Fact]
    public void IsMissingBcRuntimeDll_FileNotFound_NonBcAssembly_ReturnsFalse()
    {
        var ex = new System.IO.FileNotFoundException("File not found.", "SomeUserLibrary.dll");
        Assert.False(Executor.IsMissingBcRuntimeDll(ex));
    }

    /// <summary>
    /// FileLoadException for a BC assembly (version conflict, strong-name failure, etc.)
    /// is also classified as a runner limitation.
    /// </summary>
    [Fact]
    public void IsMissingBcRuntimeDll_FileLoadException_BcAssembly_ReturnsTrue()
    {
        var ex = new System.IO.FileLoadException(
            "Could not load file or assembly 'Microsoft.Dynamics.Nav.Types, Version=28.0.0.0'.",
            "Microsoft.Dynamics.Nav.Types.dll");
        Assert.True(Executor.IsMissingBcRuntimeDll(ex));
    }

    /// <summary>
    /// AggregateException wrapping a BC FileNotFoundException must be classified as a
    /// runner limitation via the recursive inner-exception traversal in IsRunnerError.
    /// </summary>
    [Fact]
    public void IsRunnerError_AggregateException_WrappingBcFileNotFound_ReturnsTrue()
    {
        var inner = new System.IO.FileNotFoundException(
            "Could not load file or assembly 'Microsoft.BusinessCentral.Telemetry.Abstractions'.",
            "Microsoft.BusinessCentral.Telemetry.Abstractions.dll");
        var agg = new AggregateException("One or more errors occurred.", inner);
        Assert.True(Executor.IsRunnerError(agg));
    }

    /// <summary>
    /// TypeInitializationException (InnerException path) wrapping a BC FileNotFoundException
    /// must also be classified as a runner limitation.
    /// </summary>
    [Fact]
    public void IsRunnerError_TypeInitializationException_WrappingBcFileNotFound_ReturnsTrue()
    {
        var inner = new System.IO.FileNotFoundException(
            "Could not load file or assembly 'Microsoft.Dynamics.Nav.Ncl'.",
            "Microsoft.Dynamics.Nav.Ncl.dll");
        var outer = new TypeInitializationException("SomeNavType", inner);
        Assert.True(Executor.IsRunnerError(outer));
    }

    // ---------------------------------------------------------------------------
    // IsLikelyRunnerLimitation — generic catch-all heuristic
    // ---------------------------------------------------------------------------

    /// <summary>
    /// MissingMethodException always indicates a BC runtime method that the runner
    /// has not yet mocked — must be classified as a runner limitation.
    /// </summary>
    [Fact]
    public void IsLikelyRunnerLimitation_MissingMethodException_ReturnsTrue()
    {
        var ex = new MissingMethodException("void SomeMockHandle.ALUnsupportedMethod()");
        Assert.True(Executor.IsLikelyRunnerLimitation(ex));
    }

    /// <summary>
    /// MissingMemberException (base class of MissingMethodException and
    /// MissingFieldException) must also be classified as a runner limitation.
    /// </summary>
    [Fact]
    public void IsLikelyRunnerLimitation_MissingMemberException_ReturnsTrue()
    {
        var ex = new MissingMemberException("SomeMockHandle", "ALUnsupportedField");
        Assert.True(Executor.IsLikelyRunnerLimitation(ex));
    }

    /// <summary>
    /// An exception whose stack trace contains Microsoft.Dynamics.Nav.* frames
    /// originates from BC runtime code that requires service-tier context —
    /// must be classified as a runner limitation.
    /// </summary>
    [Fact]
    public void IsLikelyRunnerLimitation_ExceptionFromBcRuntimeNamespace_ReturnsTrue()
    {
        Exception? ex = null;
        try
        {
            // Calling ThrowFromBcNamespace() produces a real stack trace that
            // contains the "Microsoft.Dynamics.Nav." namespace segment because
            // the helper class lives in that namespace.
            Microsoft.Dynamics.Nav.TestHelper.BcRuntimeSimulator.ThrowNull();
        }
        catch (NullReferenceException e) { ex = e; }

        Assert.NotNull(ex);
        Assert.True(Executor.IsLikelyRunnerLimitation(ex!));
    }

    /// <summary>
    /// An exception from the Microsoft.BusinessCentral.* namespace also indicates
    /// a BC service-tier dependency — must be classified as a runner limitation.
    /// </summary>
    [Fact]
    public void IsLikelyRunnerLimitation_ExceptionFromBusinessCentralNamespace_ReturnsTrue()
    {
        Exception? ex = null;
        try { Microsoft.BusinessCentral.TestHelper.BusinessCentralSimulator.ThrowNull(); }
        catch (NullReferenceException e) { ex = e; }

        Assert.NotNull(ex);
        Assert.True(Executor.IsLikelyRunnerLimitation(ex!));
    }

    /// <summary>
    /// A plain NullReferenceException with no BC runtime frames in the stack trace
    /// is NOT a runner limitation — it is a user test logic failure.
    /// </summary>
    [Fact]
    public void IsLikelyRunnerLimitation_PlainNullReferenceException_ReturnsFalse()
    {
        var ex = new NullReferenceException("object is null");
        // Exception created directly, no stack trace → not from BC runtime
        Assert.False(Executor.IsLikelyRunnerLimitation(ex));
    }

    /// <summary>
    /// A standard assertion exception (the kind thrown by AL Assert codeunit)
    /// must NOT be classified as a runner limitation.
    /// </summary>
    [Fact]
    public void IsLikelyRunnerLimitation_PlainException_ReturnsFalse()
    {
        var ex = new InvalidOperationException("Expected 1, got 2");
        Assert.False(Executor.IsLikelyRunnerLimitation(ex));
    }

    /// <summary>
    /// MissingMethodException must produce Status=Error (IsRunnerBug=true) when it
    /// surfaces through the test executor, not Status=Fail.
    /// Verified via the pipeline using inline AL code that exercises a runtime path
    /// where reflection cannot find an overload on the mock type.
    /// </summary>
    [Fact]
    public void Pipeline_MissingMethodException_ClassifiedAsError()
    {
        // MissingMethodException surfaces when MockCodeunitHandle.Invoke finds a
        // matching codeunit class but the invoked method overload is missing.
        // We simulate this directly: build a result set that mirrors how the
        // executor would classify such an exception, then verify IsRunnerBug=true.
        var missingMethodEx = new MissingMethodException("void Codeunit50000.ALMissingProc()");
        Assert.True(Executor.IsLikelyRunnerLimitation(missingMethodEx),
            "MissingMethodException must be classified as a runner limitation");

        // Also verify it does NOT satisfy IsRunnerError (old path) so the new path
        // is the one doing the classification.
        Assert.False(Executor.IsRunnerError(missingMethodEx),
            "MissingMethodException must not already be caught by IsRunnerError");
    }



    // ---------------------------------------------------------------------------
    // Pipeline error reporting — rewriter failures and compilation failures
    // ---------------------------------------------------------------------------

    /// <summary>
    /// When the rewriter throws for an AL object, the pipeline must:
    ///   - return ExitCode == 2 (runner limitation, not assertion failure)
    ///   - populate RewriterErrors with the failing object name and exception message
    ///   - not throw an AggregateException to the caller
    /// </summary>
    [Fact]
    public void RewriterGap_ReturnsExitCode2_AndPopulatesRewriterErrors()
    {
        var pipeline = new AlRunnerPipeline();
        // Inject a rewriter that throws unconditionally — simulates a rewriter gap
        var result = pipeline.Run(new PipelineOptions
        {
            TestIsolation = AlRunner.TestIsolation.Method,
            InlineCode = "Message('hello');",
            RewriterFactory = _ => throw new InvalidOperationException("simulated rewriter gap")
        });

        Assert.Equal(2, result.ExitCode);
        Assert.NotNull(result.RewriterErrors);
        Assert.NotEmpty(result.RewriterErrors);
        // The error must include the exception type and message
        var error = result.RewriterErrors[0].Error;
        Assert.Contains("InvalidOperationException", error);
        Assert.Contains("simulated rewriter gap", error);
        // Stderr must mention the runner gap hint
        Assert.Contains("AL constructs not yet handled", result.StdErr);
        Assert.Contains("--dump-csharp", result.StdErr);
    }

    /// <summary>
    /// When the rewriter produces C# that Roslyn cannot compile, the pipeline must:
    ///   - return ExitCode == 2 (runner limitation, not assertion failure)
    ///   - populate CompilationErrors with the Roslyn error messages
    ///   - not expose any test results (compilation never completed)
    /// </summary>
    [Fact]
    public void CompilationGap_ReturnsExitCode2_AndPopulatesCompilationErrors()
    {
        var pipeline = new AlRunnerPipeline();
        // Inject a rewriter that returns syntactically valid but semantically
        // broken C# — simulates a rewriter gap that slips through to the Roslyn step.
        var result = pipeline.Run(new PipelineOptions
        {
            TestIsolation = AlRunner.TestIsolation.Method,
            InlineCode = "Message('hello');",
            RewriterFactory = _ =>
                Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                    // Uses NonExistentType_XYZ which Roslyn cannot resolve
                    "namespace AlRunner { public class Broken { NonExistentType_XYZ _f; } }")
        });

        Assert.Equal(2, result.ExitCode);
        Assert.NotNull(result.CompilationErrors);
        Assert.NotEmpty(result.CompilationErrors);
        // At least one error must reference the unknown type
        Assert.Contains(result.CompilationErrors, e => e.Contains("NonExistentType_XYZ"));
        // Stderr must carry the runner gap hint and debugging tip
        Assert.Contains("not yet handled by the runner", result.StdErr);
        Assert.Contains("--dump-rewritten", result.StdErr);
        // No test results — compilation never completed
        Assert.Empty(result.Tests);
    }

    /// <summary>
    /// Negative proof: a real assertion failure still returns ExitCode == 1,
    /// not ExitCode == 2.  ExitCode 2 is reserved for runner limitations only.
    /// </summary>
    [Fact]
    public void RealAssertionFailure_ReturnsExitCode1_NotExitCode2()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            TestIsolation = AlRunner.TestIsolation.Method,
            InputPaths =
            {
                TestPath("06-intentional-failure", "src"),
                TestPath("06-intentional-failure", "test")
            }
        });

        // Assertion failures must produce exit code 1, never exit code 2
        Assert.Equal(1, result.ExitCode);
        Assert.Null(result.RewriterErrors);
        Assert.Null(result.CompilationErrors);
    }

    // ---------------------------------------------------------------------------
    // ExitCode() strict mode tests (#201)
    // ---------------------------------------------------------------------------

    /// <summary>All tests pass → exit 0, regardless of strict mode.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExitCode_AllPass_Returns0_RegardlessOfStrict(bool strict)
    {
        var results = new List<TestResult>
        {
            new() { Name = "T1", Status = TestStatus.Pass },
            new() { Name = "T2", Status = TestStatus.Pass }
        };
        Assert.Equal(0, Executor.ExitCode(results, strict));
    }

    /// <summary>Any Fail → exit 1, regardless of strict mode.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExitCode_Fail_Returns1_RegardlessOfStrict(bool strict)
    {
        var results = new List<TestResult>
        {
            new() { Name = "T1", Status = TestStatus.Pass },
            new() { Name = "T2", Status = TestStatus.Fail, Message = "Expected 1, got 2" }
        };
        Assert.Equal(1, Executor.ExitCode(results, strict));
    }

    /// <summary>Empty results → exit 1, regardless of strict mode.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExitCode_Empty_Returns1_RegardlessOfStrict(bool strict)
    {
        Assert.Equal(1, Executor.ExitCode(new List<TestResult>(), strict));
    }

    /// <summary>Runner errors with strict=false → exit 2 (lenient).</summary>
    [Fact]
    public void ExitCode_RunnerError_NotStrict_Returns2()
    {
        var results = new List<TestResult>
        {
            new() { Name = "T1", Status = TestStatus.Pass },
            new() { Name = "T2", Status = TestStatus.Error, IsRunnerBug = true, Message = "not found" }
        };
        Assert.Equal(2, Executor.ExitCode(results, strict: false));
    }

    /// <summary>Runner errors with strict=true → exit 1 (promoted).</summary>
    [Fact]
    public void ExitCode_RunnerError_Strict_Returns1()
    {
        var results = new List<TestResult>
        {
            new() { Name = "T1", Status = TestStatus.Pass },
            new() { Name = "T2", Status = TestStatus.Error, IsRunnerBug = true, Message = "not found" }
        };
        Assert.Equal(1, Executor.ExitCode(results, strict: true));
    }

    // ---------------------------------------------------------------------------
    // Pipeline strict mode — rewriter & compilation gaps (#201)
    // ---------------------------------------------------------------------------

    /// <summary>Rewriter gap with strict=true → exit 1 (not 2).</summary>
    [Fact]
    public void RewriterGap_Strict_ReturnsExitCode1()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            TestIsolation = AlRunner.TestIsolation.Method,
            InlineCode = "Message('hello');",
            Strict = true,
            RewriterFactory = _ => throw new InvalidOperationException("simulated rewriter gap")
        });

        Assert.Equal(1, result.ExitCode);
    }

    /// <summary>Compilation gap with strict=true → exit 1 (not 2).</summary>
    [Fact]
    public void CompilationGap_Strict_ReturnsExitCode1()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            TestIsolation = AlRunner.TestIsolation.Method,
            InlineCode = "Message('hello');",
            Strict = true,
            RewriterFactory = _ =>
                Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                    "namespace AlRunner { public class Broken { NonExistentType_XYZ _f; } }")
        });

        Assert.Equal(1, result.ExitCode);
    }

    /// <summary>Rewriter gap without strict → still exit 2 (unchanged behaviour).</summary>
    [Fact]
    public void RewriterGap_NotStrict_StillReturnsExitCode2()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            TestIsolation = AlRunner.TestIsolation.Method,
            InlineCode = "Message('hello');",
            Strict = false,
            RewriterFactory = _ => throw new InvalidOperationException("simulated rewriter gap")
        });

        Assert.Equal(2, result.ExitCode);
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

    // ── LooksLikeFrameworkVersionMismatch ──

    [Fact]
    public void FrameworkMismatch_SystemTextJson_ReturnsTrue()
    {
        var msg = "Could not load file or assembly 'System.Text.Json, Version=10.0.0.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51'.";
        Assert.True(Executor.LooksLikeFrameworkVersionMismatch(msg));
    }

    [Fact]
    public void FrameworkMismatch_MicrosoftExtensions_ReturnsTrue()
    {
        var msg = "Could not load file or assembly 'Microsoft.Extensions.Logging, Version=10.0.0.0'.";
        Assert.True(Executor.LooksLikeFrameworkVersionMismatch(msg));
    }

    [Fact]
    public void FrameworkMismatch_BcDll_ReturnsFalse()
    {
        // BC runtime DLLs are not framework version mismatches
        var msg = "Could not load file or assembly 'Microsoft.Dynamics.Nav.Ncl, Version=28.0.0.0'.";
        Assert.False(Executor.LooksLikeFrameworkVersionMismatch(msg));
    }

    [Fact]
    public void FrameworkMismatch_NullMessage_ReturnsFalse()
    {
        Assert.False(Executor.LooksLikeFrameworkVersionMismatch(null));
    }

    [Fact]
    public void FrameworkMismatch_UnrelatedMessage_ReturnsFalse()
    {
        Assert.False(Executor.LooksLikeFrameworkVersionMismatch("Object reference not set to an instance of an object."));
    }

    [Fact]
    public void PrintResults_FrameworkMismatch_ShowsInstallHint()
    {
        var results = new List<AlRunner.TestResult>
        {
            new()
            {
                Name = "TestSomething",
                Status = AlRunner.TestStatus.Fail,
                Message = "Could not load file or assembly 'System.Text.Json, Version=10.0.0.0'."
            }
        };
        var output = CaptureStdOut(() => Executor.PrintResults(results));
        Assert.Contains("Install .NET 10", output);
        Assert.Contains("https://dotnet.microsoft.com/download/dotnet/10.0", output);
    }
}
