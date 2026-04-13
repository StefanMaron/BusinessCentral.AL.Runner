using System.Text.Json;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

[Collection("Pipeline")]
public class JsonOutputTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string TestPath(string testCase, string sub) =>
        Path.Combine(RepoRoot, "tests", testCase, sub);

    // --- SerializeJsonOutput unit tests ---

    [Fact]
    public void SerializeJsonOutput_ErrorStatus_EmitsErrorNotFail()
    {
        var results = new List<TestResult>
        {
            new() { Name = "TestA", Status = TestStatus.Error, Message = "NotSupportedException: not supported" }
        };

        var json = AlRunnerPipeline.SerializeJsonOutput(results, exitCode: 1);
        var doc = JsonDocument.Parse(json);
        var test = doc.RootElement.GetProperty("tests").EnumerateArray().First();

        Assert.Equal("error", test.GetProperty("status").GetString());
    }

    [Fact]
    public void SerializeJsonOutput_ErrorStatus_CountedInErrors_NotFailed()
    {
        var results = new List<TestResult>
        {
            new() { Name = "TestPass", Status = TestStatus.Pass },
            new() { Name = "TestFail", Status = TestStatus.Fail, Message = "assertion failed" },
            new() { Name = "TestError", Status = TestStatus.Error, Message = "CompilationExcludedException: excluded" }
        };

        var json = AlRunnerPipeline.SerializeJsonOutput(results, exitCode: 1);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("passed").GetInt32());
        Assert.Equal(1, root.GetProperty("failed").GetInt32());
        Assert.Equal(1, root.GetProperty("errors").GetInt32());
        Assert.Equal(3, root.GetProperty("total").GetInt32());
    }

    [Fact]
    public void SerializeJsonOutput_WithCompilationErrors_EmitsField()
    {
        var results = new List<TestResult>
        {
            new() { Name = "TestA", Status = TestStatus.Error, Message = "CompilationExcludedException: Codeunit 50841 was excluded during compilation." }
        };
        var compilationErrors = new Dictionary<string, List<string>>
        {
            ["/tmp/XmlPort50841.cs"] = new() { "CS0246: The type or namespace name 'NavXmlPort' could not be found" }
        };

        var json = AlRunnerPipeline.SerializeJsonOutput(results, exitCode: 1, compilationErrors: compilationErrors);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("compilationErrors", out var compErrors));
        Assert.Equal(JsonValueKind.Array, compErrors.ValueKind);
        Assert.Equal(1, compErrors.GetArrayLength());

        var entry = compErrors.EnumerateArray().First();
        Assert.Equal("XmlPort50841.cs", entry.GetProperty("file").GetString());
        Assert.True(entry.TryGetProperty("errors", out var errors));
        Assert.Contains("CS0246", errors.EnumerateArray().First().GetString());
    }

    [Fact]
    public void SerializeJsonOutput_NoCompilationErrors_OmitsField()
    {
        var results = new List<TestResult>
        {
            new() { Name = "TestA", Status = TestStatus.Pass }
        };

        var json = AlRunnerPipeline.SerializeJsonOutput(results, exitCode: 0, compilationErrors: null);
        var doc = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.TryGetProperty("compilationErrors", out _));
    }

    [Fact]
    public void SerializeJsonOutput_EmptyCompilationErrors_OmitsField()
    {
        var results = new List<TestResult>
        {
            new() { Name = "TestA", Status = TestStatus.Pass }
        };

        var json = AlRunnerPipeline.SerializeJsonOutput(results, exitCode: 0,
            compilationErrors: new Dictionary<string, List<string>>());
        var doc = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.TryGetProperty("compilationErrors", out _));
    }

    // --- Integration tests via AlRunnerPipeline ---

    [Fact]
    public void OutputJson_PassingTests_EmitsJsonWithTests()
    {
        // Verify that --output-json produces valid JSON with test results when
        // compilation succeeds. Partial compilation (file exclusion) was removed
        // in #80 — compilation is now all-or-nothing.
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("84-json-compilation-error", "src"), TestPath("84-json-compilation-error", "test") },
            OutputJson = true
        });

        Assert.Equal(0, result.ExitCode);
        var doc = JsonDocument.Parse(result.StdOut.Trim());
        var root = doc.RootElement;

        Assert.True(root.GetProperty("passed").GetInt32() > 0);
        Assert.Equal(0, root.GetProperty("errors").GetInt32());
        Assert.Equal(0, root.GetProperty("failed").GetInt32());
        // compilationErrors field is absent — no per-file exclusion in all-or-nothing mode
        Assert.False(root.TryGetProperty("compilationErrors", out _));
    }

    [Fact]
    public void OutputJson_AllTestsPass_StatusIsPass()
    {
        // Every test in test-84 should get status "pass" in the JSON output.
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("84-json-compilation-error", "src"), TestPath("84-json-compilation-error", "test") },
            OutputJson = true
        });

        var doc = JsonDocument.Parse(result.StdOut.Trim());
        var tests = doc.RootElement.GetProperty("tests").EnumerateArray().ToList();

        Assert.True(tests.Count >= 2, "At least two tests should be present");
        Assert.All(tests, t => Assert.Equal("pass", t.GetProperty("status").GetString()));
    }

    [Fact]
    public void CompilationExcludedException_ProducesErrorNotFail()
    {
        // CompilationExcludedException and NotSupportedException both produce
        // status "error" in JSON, never "fail". This distinguishes tooling
        // issues from real assertion failures.
        var error = new TestResult
        {
            Name = "MyTest",
            Status = TestStatus.Error,
            Message = "CompilationExcludedException: Codeunit 131300 was excluded during compilation."
        };
        var fail = new TestResult
        {
            Name = "MyFailTest",
            Status = TestStatus.Fail,
            Message = "Expected 5 but got 3"
        };

        var json = AlRunnerPipeline.SerializeJsonOutput(new List<TestResult> { error, fail }, exitCode: 1);
        var doc = JsonDocument.Parse(json);
        var tests = doc.RootElement.GetProperty("tests").EnumerateArray().ToList();

        var errorTest = tests.First(t => t.GetProperty("name").GetString() == "MyTest");
        var failTest = tests.First(t => t.GetProperty("name").GetString() == "MyFailTest");

        Assert.Equal("error", errorTest.GetProperty("status").GetString());
        Assert.Equal("fail", failTest.GetProperty("status").GetString());

        // errors counts CompilationExcludedException, failed counts assertion failures — they are separate
        Assert.Equal(1, doc.RootElement.GetProperty("failed").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("errors").GetInt32());
    }
}
