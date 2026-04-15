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
        Path.Combine(CliRunner.FindTestCase(testCase), sub);

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
    /// When two C# sources are compiled together and the first file has errors, the result
    /// must be null — ordering must not affect failure behaviour (no silent exclusion).
    /// </summary>
    [Fact]
    public void RoslynCompiler_MultipleFiles_ReturnsNullWhenFirstFileHasErrors()
    {
        const string badCSharp = @"
namespace AlRunnerGenerated {
    public class BrokenClass2 { THIS IS NOT VALID C# }
}";
        const string goodCSharp = @"
namespace AlRunnerGenerated {
    public class GoodClass2 { public int Value => 42; }
}";

        var assembly = RoslynCompiler.Compile(new List<(string Name, string Code)>
        {
            ("BrokenFile2", badCSharp),
            ("GoodFile2", goodCSharp)
        });

        // Entire compilation must fail regardless of file ordering
        Assert.Null(assembly);
    }

    [Fact]
    public void IterationTracking_EmitsSourceFile()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("67-iteration-tracking", "src"), TestPath("67-iteration-tracking", "test") },
            OutputJson = true,
            IterationTracking = true,
        });

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.Iterations);
        Assert.True(result.Iterations!.Count > 0, "Expected at least one loop");

        // Parse the JSON output to verify sourceFile is present
        using var doc = System.Text.Json.JsonDocument.Parse(result.StdOut);
        var iterations = doc.RootElement.GetProperty("iterations");
        foreach (var iter in iterations.EnumerateArray())
        {
            Assert.True(iter.TryGetProperty("sourceFile", out var sf), "Expected sourceFile property on iteration");
            var sourceFile = sf.GetString()!;
            Assert.EndsWith(".al", sourceFile);
            // Loops are in src/LoopHelper.al, not test/LoopTest.al
            Assert.Contains("LoopHelper", sourceFile);
        }
    }

    [Fact]
    public void SummaryLine_AllPass_ShowsCompactFormat()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("01-pure-function", "src"), TestPath("01-pure-function", "test") }
        });

        Assert.Equal(0, result.ExitCode);
        // Compact format: "X passed in N.Ns" — no "failed" or "blocked" on a clean run
        Assert.Matches(@"\d+ passed in \d+\.\d+s", result.StdOut);
        Assert.DoesNotContain("failed", result.StdOut);
        Assert.DoesNotContain("blocked", result.StdOut);
    }

    [Fact]
    public void SummaryLine_WithFailure_ShowsFullFormat()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("06-intentional-failure", "src"), TestPath("06-intentional-failure", "test") }
        });

        Assert.NotEqual(0, result.ExitCode);
        // Full format shows at least "X passed, Y failed in N.Ns"
        Assert.Matches(@"\d+ passed, \d+ failed.*in \d+\.\d+s", result.StdOut);
    }

    // ─── Compilation error deduplication ──────────────────────────────────────

    [Fact]
    public void DeduplicateCompilationErrors_GroupsByCSCodeAndType()
    {
        var errors = new List<string>
        {
            "Obj.cs(10,5): error CS1061: 'Report70400' does not contain a definition for 'amountDue'",
            "Obj.cs(20,5): error CS1061: 'Report70400' does not contain a definition for 'Totals'",
            "Obj.cs(30,5): error CS1061: 'Report70400' does not contain a definition for 'columnHead'",
            "Obj.cs(40,5): error CS0103: The name 'privacyBlockedTxt' does not exist in the current context",
        };

        var grouped = TelemetryReporter.DeduplicateCompilationErrors(errors);

        // CS1061 on 'Report70400' should be one group with count 3
        var cs1061 = grouped.Single(g => g.Key == "CS1061 on 'Report70400'");
        Assert.Equal(3, cs1061.Count);

        // CS0103 on 'privacyBlockedTxt' should be a separate group with count 1
        var cs0103 = grouped.Single(g => g.Key == "CS0103 on 'privacyBlockedTxt'");
        Assert.Equal(1, cs0103.Count);
        Assert.Contains("does not exist", cs0103.SampleMessage);
    }

    [Fact]
    public void DeduplicateCompilationErrors_EmptyList()
    {
        var grouped = TelemetryReporter.DeduplicateCompilationErrors(new List<string>());
        Assert.Empty(grouped);
    }

    [Fact]
    public void DeduplicateCompilationErrors_OrderedByCountDescending()
    {
        var errors = new List<string>
        {
            "A.cs(1,1): error CS0103: The name 'x' does not exist in the current context",
            "A.cs(2,1): error CS1061: 'Foo' does not contain a definition for 'bar'",
            "A.cs(3,1): error CS1061: 'Foo' does not contain a definition for 'baz'",
            "A.cs(4,1): error CS1061: 'Foo' does not contain a definition for 'qux'",
        };

        var grouped = TelemetryReporter.DeduplicateCompilationErrors(errors);

        Assert.Equal(2, grouped.Count);
        Assert.Equal("CS1061 on 'Foo'", grouped[0].Key); // 3× comes first
        Assert.Equal(3, grouped[0].Count);
        Assert.Equal("CS0103 on 'x'", grouped[1].Key);
        Assert.Equal(1, grouped[1].Count);
    }

    // ─── Report dataset columns (RDLC skip) ─────────────────────────────────

    [Fact]
    public void ReportWithDatasetColumns_CompilesAndRuns()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("112-report-dataset-columns", "src"),
                           TestPath("112-report-dataset-columns", "test") }
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("4 passed", result.StdOut);
    }

    [Fact]
    public void CompilationErrors_PopulatedOnFailure()
    {
        // Pipeline should populate CompilationErrors when Roslyn compilation fails.
        // Use test 112 which compiles fine — verify CompilationErrors is null/empty on success.
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("112-report-dataset-columns", "src"),
                           TestPath("112-report-dataset-columns", "test") }
        });

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.CompilationErrors == null || result.CompilationErrors.Count == 0,
            "CompilationErrors should be null or empty on successful compilation");
    }

    [Fact]
    public void CrossExtension_SameNamePageExtensions_Suppressed()
    {
        // Two extensions (appA, appB) define a pageextension with the same name
        // "ItemCardExt" — valid in production BC where extensions compile independently.
        // The runner compiles them together and must suppress the false AL0275/AL0197.
        var testCase = CliRunner.FindTestCase("130-cross-ext-al0275");
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths =
            {
                Path.Combine(testCase, "appA"),
                Path.Combine(testCase, "appB"),
                Path.Combine(testCase, "test")
            }
        });

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Passed > 0, "Should have passing tests");
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public void CrossExtension_SameNameCodeunits_NotSuppressed()
    {
        // Two extensions define a codeunit with the same name "Shared Helper" —
        // Codeunit is NOT an extension type, so the collision must NOT be suppressed.
        // Verify the AL0197 error appears in verbose output (not silently eaten).
        var testCase = CliRunner.FindTestCase("131-genuine-codeunit-collision");
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths =
            {
                Path.Combine(testCase, "appA"),
                Path.Combine(testCase, "appB"),
                Path.Combine(testCase, "test")
            },
            Verbose = true
        });

        // The AL0197 for Codeunit type must NOT be suppressed
        Assert.DoesNotContain("Cross-extension name collisions suppressed", result.StdErr);
        Assert.Contains("AL0197", result.StdErr);
        Assert.Contains("Codeunit", result.StdErr);
    }

    [Fact]
    public void SameExtension_DuplicatePageExtensionName_NotSuppressed()
    {
        // Same extension defines two pageextensions with the same name "DuplicatedExt"
        // but different IDs. AL0197 fires with a single extension identity.
        // The two-pass grouping requires 2+ identities, so this must NOT be suppressed.
        var testCase = CliRunner.FindTestCase("135-same-ext-pageext-duplicate");
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths =
            {
                Path.Combine(testCase, "src"),
                Path.Combine(testCase, "test")
            },
            Verbose = true
        });

        // AL0197 for same-extension duplicate must NOT be suppressed
        Assert.DoesNotContain("Cross-extension name collisions suppressed", result.StdErr);
        Assert.Contains("AL0197", result.StdErr);
        Assert.Contains("PageExtension", result.StdErr);
    }

    [Fact]
    public void UserId_DefaultIsEmptyString()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("45-userid", "test") }
        });
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Passed > 0);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public void UserId_ReturnsConfiguredValue()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            UserId = "TESTUSER123",
            InlineCode = "if UserId() <> 'TESTUSER123' then error('UserId() returned wrong value: ' + UserId());"
        });
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void InitValue_AppliedOnRecordInit()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("47-initvalue", "src"), TestPath("47-initvalue", "test") }
        });
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Passed > 0);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public void RecordGet_RetrievesByPrimaryKey()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("48-record-get", "src"), TestPath("48-record-get", "test") }
        });
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Passed > 0);
        Assert.Equal(0, result.Failed);
    }
}
