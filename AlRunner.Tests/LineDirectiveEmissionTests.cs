using AlRunner;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Integration tests verifying that when <see cref="PipelineOptions.EmitLineDirectives"/>
/// is enabled, the rewritten C# contains <c>#line N "*.al"</c> directives and the
/// compiled assembly's portable PDB maps IL sequence points back to .al file paths.
/// </summary>
[Collection("Pipeline")]
public class LineDirectiveEmissionTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string TestPath(string sub) =>
        Path.Combine(RepoRoot, "tests", "protocol-v2-line-directives", sub);

    [Fact]
    public void Transpile_EmitsLineDirectivesWithAlPaths()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("src"), TestPath("test") },
            EmitLineDirectives = true,
            EmitGeneratedCSharp = true,
        });

        // ExitCode may be non-zero because FailingTest intentionally fails.
        // A compilation error would be ExitCode 2 (or 3 for transpile error).
        // We only require that the run completed (not a path/compile error).
        Assert.True(result.ExitCode == 0 || result.ExitCode == 1,
            $"Pipeline should complete (pass or test-fail), got ExitCode={result.ExitCode}. StdErr: {result.StdErr}");
        Assert.NotEmpty(result.GeneratedCSharpFiles);
        var hasDirective = result.GeneratedCSharpFiles
            .Any(c => System.Text.RegularExpressions.Regex.IsMatch(c, @"#line \d+ "".+\.al"""));
        Assert.True(hasDirective,
            "Generated C# must contain at least one #line directive referencing a .al file.");
    }

    [Fact]
    public void Transpile_AllLineDirectives_AreQuoted()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("src"), TestPath("test") },
            EmitLineDirectives = true,
            EmitGeneratedCSharp = true,
        });

        // ExitCode may be 1 due to FailingTest; compilation still succeeded.
        Assert.True(result.ExitCode == 0 || result.ExitCode == 1,
            $"Pipeline should complete (pass or test-fail), got ExitCode={result.ExitCode}. StdErr: {result.StdErr}");

        var directives = string.Join("\n", result.GeneratedCSharpFiles)
            .Split('\n')
            .Where(line => line.TrimStart().StartsWith("#line "))
            .ToList();

        Assert.NotEmpty(directives);
        foreach (var d in directives)
        {
            // Must match: #line <digits> "<path ending in .al>"
            Assert.Matches(@"#line \d+ "".+\.al""\s*$",
                d.Trim().Replace("\r", ""));
        }
    }

    [Fact]
    public void RunFailingTest_ProducesAlFileInStackTrace()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("src"), TestPath("test") },
            EmitLineDirectives = true,
        });

        // FailingTest throws via Error('intentional failure'). After #line + portable PDB,
        // the captured stack trace must contain the .al file name.
        var failed = result.Tests.First(t => t.Status == TestStatus.Fail);
        Assert.NotNull(failed.StackTrace);
        Assert.Contains(".al", failed.StackTrace, StringComparison.OrdinalIgnoreCase);
    }
}
