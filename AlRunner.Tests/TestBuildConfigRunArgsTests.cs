// TestBuildConfigRunArgsTests — proves RunArgs invokes the built al-runner.dll directly
// (`dotnet <dll>`) instead of paying MSBuild evaluation on every spawn via
// `dotnet run --no-build --project ... --` (see issue #1808).
//
// Positive: the resolved path exists on disk, ends in "al-runner.dll", and carries none
// of the `dotnet run` tokens — so a regression back to `dotnet run --no-build --project`
// fails this test outright rather than merely being slower.
//
// Negative: resolving against a project directory with no build output throws, naming the
// searched path, per .claude/rules/loud-failures.md — a silent fallback to `dotnet run`
// would hide the very regression this change removes.

using Xunit;

namespace AlRunner.Tests;

public sealed class TestBuildConfigRunArgsTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    [Fact]
    public void RunArgs_ResolvesToBuiltAssembly_NotDotnetRun()
    {
        var args = TestBuildConfig.RunArgs(ProjectPath);

        // Extract the path token (RunArgs quotes it) and confirm it's a real file on disk.
        var path = args.Trim('"');
        Assert.True(File.Exists(path), $"expected built assembly at '{path}'");
        Assert.EndsWith("al-runner.dll", path);

        // A regression back to `dotnet run --no-build --project ... --` must fail here,
        // not just run slower. Match "run" as a standalone token, not as a substring of
        // "al-runner.dll" itself.
        Assert.False(
            System.Text.RegularExpressions.Regex.IsMatch(args, @"(?<![\w-])run(?![\w-])"),
            $"expected no standalone 'run' token in '{args}'");
        Assert.DoesNotContain("--no-build", args);
        Assert.DoesNotContain("--project", args);
    }

    [Fact]
    public void RunArgs_MissingBuildOutput_ThrowsNamingSearchedPath()
    {
        var missingProjectDir = Path.Combine(
            Path.GetTempPath(), "al-runner-issue-1808-" + Guid.NewGuid().ToString("N"));

        var ex = Assert.Throws<FileNotFoundException>(
            () => TestBuildConfig.RunArgs(missingProjectDir));

        Assert.Contains(missingProjectDir, ex.Message);
        Assert.Contains("al-runner.dll", ex.Message);
    }
}
