using System.Text.Json;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

[Collection("Pipeline")]
public class PipelineTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string TestPath(string testCase, string sub) =>
        Path.Combine(RepoRoot, "tests", testCase, sub);

    [Fact]
    public void PureFunction_AllTestsPass()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("01-pure-function", "src"), TestPath("01-pure-function", "test") }
        });

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void IntentionalFailure_ReturnsNonZero()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("06-intentional-failure", "src"), TestPath("06-intentional-failure", "test") }
        });

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public void NoSource_ReturnsError()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("no AL source", result.StdErr);
    }

    [Fact]
    public void NonexistentPath_ReturnsError()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { "/nonexistent/path" }
        });

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("not found", result.StdErr);
    }

    [Fact]
    public void InlineCode_Executes()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InlineCode = "Message('hello');"
        });

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void RecordOperations_Pass()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("02-record-operations", "src"), TestPath("02-record-operations", "test") }
        });

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void InterfaceInjection_Pass()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("03-interface-injection", "src"), TestPath("03-interface-injection", "test") }
        });

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void DumpCSharp_ProducesOutput()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("01-pure-function", "src") },
            DumpCSharp = true
        });

        Assert.Contains("Generated C#", result.StdOut);
    }

    [Fact]
    public void DumpRewritten_ProducesOutput()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("01-pure-function", "src") },
            DumpRewritten = true
        });

        Assert.Contains("Rewritten C#", result.StdOut);
    }

    // --- Feature 1: Structured test results ---

    [Fact]
    public void TestResults_PopulatedForPassingTests()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("01-pure-function", "src"), TestPath("01-pure-function", "test") }
        });

        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(result.Tests);
        Assert.Equal(6, result.Passed);
        Assert.Equal(0, result.Failed);
        Assert.All(result.Tests, t =>
        {
            Assert.Equal(TestStatus.Pass, t.Status);
            Assert.NotEmpty(t.Name);
            Assert.True(t.DurationMs >= 0);
        });
    }

    [Fact]
    public void TestResults_PopulatedForFailingTests()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("06-intentional-failure", "src"), TestPath("06-intentional-failure", "test") }
        });

        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEmpty(result.Tests);
        Assert.True(result.Failed > 0);
        var failedTest = result.Tests.First(t => t.Status == TestStatus.Fail);
        Assert.NotNull(failedTest.Message);
        Assert.NotEmpty(failedTest.Message);
    }

    [Fact]
    public void OutputJson_ProducesValidJson()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("01-pure-function", "src"), TestPath("01-pure-function", "test") },
            OutputJson = true
        });

        Assert.Equal(0, result.ExitCode);
        // StdOut should contain valid JSON
        var json = result.StdOut.Trim();
        Assert.StartsWith("{", json);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Must have tests array
        Assert.True(root.TryGetProperty("tests", out var tests));
        Assert.Equal(JsonValueKind.Array, tests.ValueKind);
        Assert.Equal(6, tests.GetArrayLength());

        // Each test must have name, status, durationMs
        foreach (var test in tests.EnumerateArray())
        {
            Assert.True(test.TryGetProperty("name", out _));
            Assert.True(test.TryGetProperty("status", out var status));
            Assert.Equal("pass", status.GetString());
            Assert.True(test.TryGetProperty("durationMs", out _));
        }

        // Must have summary
        Assert.True(root.TryGetProperty("passed", out var passed));
        Assert.Equal(6, passed.GetInt32());
        Assert.True(root.TryGetProperty("failed", out var failed));
        Assert.Equal(0, failed.GetInt32());
    }

    [Fact]
    public void OutputJson_FailingTests_IncludesMessage()
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
        Assert.True(failedTest.TryGetProperty("message", out var msg));
        Assert.False(string.IsNullOrEmpty(msg.GetString()));
    }

    // --- Regression tests for Issue #66: no silent file exclusion during compilation ---

    /// <summary>
    /// When two C# sources are compiled together and one has errors, the compiler
    /// must return null (failure) rather than silently excluding the bad file and
    /// returning an assembly from only the good file.
    /// </summary>
    [Fact]
    public void RoslynCompiler_MultipleFiles_ReturnsNullWhenAnyFileHasErrors()
    {
        const string goodCSharp = @"
namespace AlRunnerGenerated {
    public class GoodClass { public int Value => 42; }
}";
        const string badCSharp = @"
namespace AlRunnerGenerated {
    public class BrokenClass { THIS IS NOT VALID C# }
}";

        var assembly = RoslynCompiler.Compile(new List<(string Name, string Code)>
        {
            ("GoodFile", goodCSharp),
            ("BrokenFile", badCSharp)
        });

        // Must fail: any compilation error in any file must cause null return
        Assert.Null(assembly);
    }

    /// <summary>
    /// When two C# sources are compiled together and one has errors, the result
    /// must be null — the good file must NOT be compiled in isolation (no silent exclusion).
    /// </summary>
    [Fact]
    public void RoslynCompiler_MultipleFiles_ReturnsNullWhenSecondFileHasErrors()
    {
        const string goodCSharp = @"
namespace AlRunnerGenerated {
    public class GoodClass2 { public int Value => 42; }
}";
        const string badCSharp = @"
namespace AlRunnerGenerated {
    public class BrokenClass2 { THIS IS NOT VALID C# }
}";

        var assembly = RoslynCompiler.Compile(new List<(string Name, string Code)>
        {
            ("GoodFile2", goodCSharp),
            ("BrokenFile2", badCSharp)
        });

        // Entire compilation must fail — not just the bad file silently excluded
        Assert.Null(assembly);
    }
}
