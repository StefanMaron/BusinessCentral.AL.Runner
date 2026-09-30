// WatchAffectedReportTests — #5027: what a `--watch --affected` cycle says about its selection, and
// the option combinations the runner refuses. No BC artifacts needed. docs/watch-affected.md.
using System.Diagnostics;
using System.Text;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public class WatchAffectedReportTests
{
    [Fact]
    public void Describe_NarrowedCycle_CountsEverySkipAndNamesTheStillFailingOnes()
    {
        var lines = WatchAffectedReport.Describe(
            new ServerSelection("affected", 2, 5, new[] { "Codeunit 50100 Helper" }, false, null, 1),
            new[] { "Codeunit50110.StillBroken" });

        Assert.Equal(new[]
        {
            "[watch] affected: ran 2 of 7   skipped-unaffected 4   skipped-failing 1",
            "[watch] affected: changed: Codeunit 50100 Helper",
            "[watch] affected: not re-run, still failing from an earlier cycle: Codeunit50110.StillBroken",
        }, lines);
    }

    [Fact]
    public void Describe_ForcedFullCycle_SaysWhyAndNotWhatChanged()
    {
        var lines = WatchAffectedReport.Describe(
            new ServerSelection("affected", 7, 0, new[] { "Codeunit 50100 Helper" }, true,
                "no previous per-test coverage baseline for this bundle", 0),
            Array.Empty<string>());

        Assert.Equal(new[]
        {
            "[watch] affected: ran 7 of 7   skipped-unaffected 0   skipped-failing 0",
            "[watch] affected: full run — no previous per-test coverage baseline for this bundle",
        }, lines);
    }

    // #5028: a cycle that used a baseline from another environment says so on its own line, which
    // the dashboard renders in its warning style.
    [Fact]
    public void Describe_DriftedCycle_WarnsNamingBothBuildsAndTheChangedObject()
    {
        var drift = new EnvironmentDriftInfo("27.5.1.0", "28.4.2.0", 1, EnvironmentDriftInfo.Diffed,
            new[] { "Codeunit 80 Sales-Post" }, null);
        var lines = WatchAffectedReport.Describe(
            new ServerSelection("affected", 1, 6, Array.Empty<string>(), false, null, 0, drift),
            Array.Empty<string>());

        Assert.Equal(2, lines.Count);
        Assert.True(WatchAffectedReport.IsWarning(lines[1]), lines[1]);
        Assert.False(WatchAffectedReport.IsWarning(lines[0]), lines[0]);
        Assert.Contains("BC 27.5.1.0, now BC 28.4.2.0", lines[1]);
        Assert.Contains("1 object(s) differ: Codeunit 80 Sales-Post", lines[1]);
    }

    [Fact]
    public void Describe_NoSelection_SaysNoBundleReachedTests()
        => Assert.Equal(new[] { "[watch] affected: no selection was made (no bundle reached test execution)" },
            WatchAffectedReport.Describe(null, Array.Empty<string>()));

    [Fact]
    public void Summary_CarriesSkippedStillFailingTestsOnTheFailedFigure()
    {
        var bucket = new BucketResult("/b", BucketStage.Ran, Array.Empty<string>(), null,
            new[] { new TestResult("Codeunit1", "Passes", TestOutcome.Pass, null, null, TimeSpan.Zero) },
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, RanGroupCount: 1);

        var narrowed = new StringWriter();
        Reporter.PrintSummary(new[] { bucket }, narrowed, default, new Reporter.SummaryOptions(StillFailingNotRerun: 2));
        Assert.Contains("Tests: 1   passed 1   failed 0 (+2 still failing, not re-run)   errors 0", narrowed.ToString());

        var plain = new StringWriter();
        Reporter.PrintSummary(new[] { bucket }, plain, default, new Reporter.SummaryOptions());
        Assert.Contains("Tests: 1   passed 1   failed 0   errors 0", plain.ToString());
    }

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static (int ExitCode, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")) + " " + string.Join(' ', args),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        using var p = Process.Start(psi)!;
        var err = new StringBuilder();
        p.OutputDataReceived += (_, _) => { };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(120_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"al-runner did not exit within 120s for: {string.Join(' ', args)}");
        }
        p.WaitForExit();
        lock (err) return (p.ExitCode, err.ToString());
    }

    [Theory]
    [InlineData("--affected", "--affected is only valid with --watch")]
    [InlineData("--watch --include-failing", "--include-failing is only valid with --watch --affected")]
    [InlineData("--watch --strict-environment", "--strict-environment is only valid with --watch --affected")]
    [InlineData("--watch --affected --tdd", "--affected cannot be combined with --tdd")]
    [InlineData("--watch --affected --per-suite", "--affected cannot be combined with --per-suite")]
    [InlineData("--watch --affected --test Foo", "--affected cannot be combined with --test/--filter")]
    public void RefusedCombination_ExitsTwoNamingIt(string flags, string message)
    {
        var bundle = TestScratch.Dir("al-runner-watch-affected-refused");
        Directory.CreateDirectory(bundle);
        var (exit, stderr) = RunCli(new[] { $"\"{bundle}\"" }.Concat(flags.Split(' ')).ToArray());
        Assert.True(exit == 2, $"exit {exit}:\n{stderr}");
        Assert.Contains(message, stderr, StringComparison.Ordinal);
    }
}
