using AlRunner;
using System.Text.RegularExpressions;
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
            .Any(c => Regex.IsMatch(c, @"#line \d+ "".+\.al"""));
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
        // the captured stack trace must reference the .al file and the correct AL line.
        var failed = result.Tests.First(t => t.Status == TestStatus.Fail);
        Assert.NotNull(failed.StackTrace);

        // The file name must appear in the stack trace.
        Assert.Contains("CalcTest.Codeunit.al", failed.StackTrace, StringComparison.OrdinalIgnoreCase);

        // The AL line for Error('intentional failure') is line 18 in the fixture.
        // The Roslyn PDB maps the Error() call to the next line after the #line 18
        // directive (which precedes StmtHit), so the stack trace reports line 19.
        // FormatSingleFrame renders as `line N in <file>` — assert that shape with
        // line 18 or 19 (the call site) referencing the AL file.
        Assert.True(
            Regex.IsMatch(failed.StackTrace, @"line\s*1[89]\s+in\s+CalcTest\.Codeunit\.al"),
            $"Expected stack trace to contain 'line 18 or 19 in CalcTest.Codeunit.al'. Got:\n{failed.StackTrace}");
    }

    [Fact]
    public void Transpile_WithoutFlag_OmitsLineDirectives()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("src"), TestPath("test") },
            EmitLineDirectives = false,
            EmitGeneratedCSharp = true,
        });
        // ExitCode 1 is expected because FailingTest fails intentionally.
        Assert.True(result.ExitCode == 0 || result.ExitCode == 1,
            $"Pipeline should complete (pass or test-fail), got ExitCode={result.ExitCode}. StdErr: {result.StdErr}");
        Assert.NotEmpty(result.GeneratedCSharpFiles);
        var combined = string.Join("\n", result.GeneratedCSharpFiles);
        // No #line directive should appear when the flag is off.
        Assert.DoesNotContain("#line ", combined);
    }

    [Fact]
    public void Transpile_LineDirectiveNumbers_MatchAlSourceLines()
    {
        // Read the fixture to discover which line contains Error('intentional failure'),
        // so the test stays robust if the fixture is edited.
        var alPath = TestPath("test") + "/CalcTest.Codeunit.al";
        var alLines = File.ReadAllLines(alPath);
        int failingLine = -1;
        for (var i = 0; i < alLines.Length; i++)
        {
            if (alLines[i].Contains("Error('intentional failure')"))
            {
                failingLine = i + 1; // 1-based
                break;
            }
        }
        Assert.True(failingLine > 0, "Could not locate Error('intentional failure') in fixture.");

        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("src"), TestPath("test") },
            EmitLineDirectives = true,
            EmitGeneratedCSharp = true,
        });
        // ExitCode 1 is expected because FailingTest fails intentionally.
        Assert.True(result.ExitCode == 0 || result.ExitCode == 1,
            $"Pipeline should complete (pass or test-fail), got ExitCode={result.ExitCode}. StdErr: {result.StdErr}");

        var combined = string.Join("\n", result.GeneratedCSharpFiles);
        var rx = new Regex(
            $@"#line\s+{failingLine}\s+""[^""]*CalcTest\.Codeunit\.al""");
        Assert.True(rx.IsMatch(combined),
            $"Expected a #line directive with line {failingLine} pointing at CalcTest.Codeunit.al. Got:\n{combined.Substring(0, Math.Min(2000, combined.Length))}");
    }

    [Fact]
    public void Transpile_CStmtHit_GetsLineDirective()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths = { TestPath("src"), TestPath("test") },
            EmitLineDirectives = true,
            EmitGeneratedCSharp = true,
        });
        // ExitCode 1 is expected because FailingTest fails intentionally.
        Assert.True(result.ExitCode == 0 || result.ExitCode == 1,
            $"Pipeline should complete (pass or test-fail), got ExitCode={result.ExitCode}. StdErr: {result.StdErr}");

        var combined = string.Join("\n", result.GeneratedCSharpFiles);
        // After a #line directive, the next non-trivia line that mentions CStmtHit
        // means the C-style hit anchor inherits an AL source position.
        Assert.Matches(
            @"#line\s+\d+\s+""[^""]*\.al""[\r\n\s]+[^\r\n]*CStmtHit\(",
            combined);
    }

    [Fact]
    public void Transpile_PathWithSpaces_EmitsValidQuotedDirective()
    {
        var pipeline = new AlRunnerPipeline();
        var result = pipeline.Run(new PipelineOptions
        {
            InputPaths =
            {
                Path.Combine(RepoRoot, "tests", "protocol-v2-line-directives-spaces", "src with space"),
                Path.Combine(RepoRoot, "tests", "protocol-v2-line-directives-spaces", "test"),
            },
            EmitLineDirectives = true,
            EmitGeneratedCSharp = true,
        });
        // ExitCode 1 is expected because FailingTest fails intentionally.
        Assert.True(result.ExitCode == 0 || result.ExitCode == 1,
            $"Pipeline should complete (pass or test-fail), got ExitCode={result.ExitCode}. StdErr: {result.StdErr}");

        var combined = string.Join("\n", result.GeneratedCSharpFiles);
        // At least one directive should reference the space-containing path.
        Assert.Matches(
            @"#line\s+\d+\s+""[^""]*src with space[/\\]Calc\.Codeunit\.al""",
            combined);
    }
}
