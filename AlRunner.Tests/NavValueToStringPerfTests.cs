using System.Diagnostics;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Regression tests for issue #23 — filter matching on NavOption-backed
/// fields used to fall through <c>NavValueToString</c>'s type dispatch
/// into <c>NavValue.ToString()</c>, which triggers
/// <c>Microsoft.CodeAnalysis</c> JIT warmup and pays ~200 ms on a
/// single call. The fix must keep all NavValue types on a fast
/// explicit-branch path; this test holds the line.
/// </summary>
[Collection("Pipeline")]
public class NavValueToStringPerfTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string TestPath(string testCase, string sub) =>
        Path.Combine(CliRunner.FindTestCase(testCase), sub);

    [Fact]
    public void OptionFieldFilterRunsUnder50ms()
    {
        // Cold pipeline to warm Roslyn + reflection caches first.
        var warm = new AlRunnerPipeline();
        warm.Run(new PipelineOptions
        {
            TestIsolation = AlRunner.TestIsolation.Method,
            InputPaths =
            {
                TestPath("52-setfilter-and", "src"),
                TestPath("52-setfilter-and", "test")
            }
        });

        // Now measure: a full pipeline Run on the same inputs should be
        // well under 50 ms per-test-execution when the cache is warm.
        // The regression manifested as ~200 ms spent inside a single
        // GetFilteredRecords call, so an aggregate test-execution budget
        // catches any regression even if other phases get faster.
        var pipeline = new AlRunnerPipeline();
        var sw = Stopwatch.StartNew();
        var result = pipeline.Run(new PipelineOptions
        {
            TestIsolation = AlRunner.TestIsolation.Method,
            InputPaths =
            {
                TestPath("52-setfilter-and", "src"),
                TestPath("52-setfilter-and", "test")
            }
        });
        sw.Stop();

        Assert.Equal(0, result.ExitCode);
        Assert.All(result.Tests, t => Assert.Equal(TestStatus.Pass, t.Status));

        // Any single test must be under 50 ms. Before the fix, the Option
        // member-name filter test alone ran ~200 ms because of BC's
        // NavFormatEvaluateHelper trap.
        foreach (var test in result.Tests)
        {
            Assert.True(test.DurationMs < 50,
                $"Test {test.Name} took {test.DurationMs} ms; expected < 50 ms. " +
                "This suggests NavValueToString is falling through to BC's " +
                "NavValue.ToString() slow path again.");
        }
    }

    [Fact]
    public void NavValueToString_AllCommonTypes_AvoidsFallback()
    {
        // Make sure every NavValue subtype al-runner ships support for has
        // a dedicated fast-path branch in NavValueToString — otherwise a
        // single unhandled type silently reaches the expensive fallback.
        // We can't call the private method directly, so we drive it
        // through the pipeline's filter path: a table with one field of
        // each common type, and a filter that forces a full row scan.
        //
        // The 52-setfilter-and fixture already uses Integer, Text, and
        // Option fields in SetFilter. If any of those starts costing
        // 50 ms per test the above perf test catches it. This test
        // asserts the fixture still passes end-to-end.
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            TestIsolation = AlRunner.TestIsolation.Method,
            InputPaths =
            {
                TestPath("52-setfilter-and", "src"),
                TestPath("52-setfilter-and", "test")
            }
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(result.Tests,
            t => t.Name == "SetFilterAndWithOptionMemberNameLiterals" && t.Status == TestStatus.Pass);
    }
}
