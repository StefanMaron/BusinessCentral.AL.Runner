// RunSummaryOutputTests — what a DEFAULT run prints for one app with one passing and one
// failing test (#4559: #4562, #4563, #4566). The mock the owner approved is in #4559.
//
// Every assertion here is about the runner's own console output — nothing in this file is a
// claim about Business Central, so none of it belongs in the al-language corpus.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class RunSummaryOutputTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string Fixture =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "RunSummaryOnePassOneFail");

    private static (string Stdout, string Stderr, int Exit) Run(string bundleArg, params string[] extraArgs)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        foreach (var a in extraArgs) args.Append(' ').Append(a);
        args.Append($" --cache \"{TestScratch.Dir("al-runner-run-summary-output")}\"");
        args.Append($" \"{bundleArg}\"");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        // The DEFAULT output is the subject: nothing the test host or the shell set may leak in.
        psi.Environment.Remove(SpawnedRunnerShowsPassLines.Variable);
        psi.Environment.Remove("AL_RUNNER_VERBOSE");
        psi.Environment.Remove("AL_RUNNER_FAILURES_ONLY");
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(240_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        return (stdout.Result.Replace("\r\n", "\n"), stderr.Result.Replace("\r\n", "\n"), p.ExitCode);
    }

    private static string[] Lines(string s) => s.Split('\n');

    /// <summary>
    /// #4563 (owner decision 2026-09-25): PASS lines are hidden by default; the failure entry
    /// and the counts still say what passed.
    /// </summary>
    [SkippableFact]
    public void DefaultRun_ListsTheFailure_NotThePass()
    {
        TestArtifacts.SkipIfMissing();
        var (stdout, stderr, exit) = Run(Fixture);

        Assert.True(exit == 1, $"one test fails by construction, so exit 1:\n{stdout}\n{stderr}");
        Assert.DoesNotContain(Lines(stdout), l => l.StartsWith("PASS ", StringComparison.Ordinal));
        Assert.True(RunnerFailureLines.Failed(stdout, 50150, "CustomerNameFails"), stdout);
        Assert.Contains("Tests: 2   passed 1   failed 1   errors 0 ", stdout);
    }

    /// <summary>#4563's other half: --show-pass brings the PASS lines back.</summary>
    [SkippableFact]
    public void ShowPass_ListsThePassingTest()
    {
        TestArtifacts.SkipIfMissing();
        var (stdout, _, _) = Run(Fixture, "--show-pass");

        Assert.Contains(Lines(stdout),
            l => Regex.IsMatch(l, @"^PASS +Codeunit50150\.CustomerInsertPasses \(\d+ms\)$"));
    }

    /// <summary>
    /// #4566: the failure entry leads with the codeunit's NAME and keeps its id in the
    /// parenthesis, and BC's dialog-exception type name is not in front of the message.
    /// </summary>
    [SkippableFact]
    public void FailureEntry_NamesTheCodeunit_AndDropsTheDialogExceptionType()
    {
        TestArtifacts.SkipIfMissing();
        var (stdout, _, _) = Run(Fixture);

        var heading = Assert.Single(RunnerFailureLines.All(stdout));
        Assert.Matches(@"^FAIL  ""Probe Customer Test""\.CustomerNameFails \(Codeunit50150, \d+ ms\)$", heading);

        var lines = Lines(stdout);
        var message = lines[Array.IndexOf(lines, heading) + 1];
        Assert.Equal("      customer name: expected Expected, got Actual", message);
        Assert.DoesNotContain("NavNCLDialogException", stdout);
        // The AL call stack is unchanged.
        Assert.Contains("\"Probe Customer Test\"(CodeUnit 50150).CustomerNameFails line 2", stdout);
    }

    /// <summary>#4566: the machine-readable outputs keep BC's message exactly as raised.</summary>
    [SkippableFact]
    public void OutputJson_KeepsTheFullMessage()
    {
        TestArtifacts.SkipIfMissing();
        var junit = Path.Combine(TestScratch.Dir("al-runner-run-summary-output-junit"), "r.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(junit)!);
        var (stdout, _, _) = Run(Fixture, "--output-json", "--output-junit", $"\"{junit}\"");

        var doc = JsonDocument.Parse(stdout);
        var failed = doc.RootElement.GetProperty("tests").EnumerateArray()
            .Single(t => t.GetProperty("status").GetString() == "fail");
        Assert.Equal("NavNCLDialogException: customer name: expected Expected, got Actual",
            failed.GetProperty("message").GetString());
        Assert.Contains("NavNCLDialogException: customer name: expected Expected, got Actual",
            File.ReadAllText(junit));
    }

    /// <summary>
    /// #4562: one app prints one counts line, the seed with a replay command, and a last line
    /// saying what the exit code means — and none of the per-app lines that repeat it.
    /// </summary>
    [SkippableFact]
    public void DefaultRun_EndsWithCountsSeedAndResult_AndNoPerAppLines()
    {
        TestArtifacts.SkipIfMissing();
        var (stdout, _, exit) = Run(Fixture);
        var lines = Lines(stdout.TrimEnd('\n'));

        Assert.Equal(1, exit);
        Assert.Equal("Result: FAILED, exit code 1 (at least one test failed or errored)", lines[^1]);

        var counts = Assert.Single(lines, l => l.StartsWith("Tests: ", StringComparison.Ordinal));
        Assert.Matches(@"^Tests: 2   passed 1   failed 1   errors 0        Time: [\d.]+ s \(wall [\d.]+ s\)$", counts);

        var seedLine = Assert.Single(lines, l => l.StartsWith("Seed:", StringComparison.Ordinal));
        var seed = Regex.Match(seedLine, @"^Seed:  (\d+)   replay one failure: al-runner --seed (\d+) --test Codeunit50150\.CustomerNameFails ");
        Assert.True(seed.Success, seedLine);
        Assert.Equal(seed.Groups[1].Value, seed.Groups[2].Value);

        // The per-app lines one app does not need.
        Assert.DoesNotContain(lines, l => l.StartsWith("=== ", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("[1/1]", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("1P/1F/0E", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("Buckets:", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("Apps:", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("AL emit:", StringComparison.Ordinal));
    }

    /// <summary>
    /// #4562's bug: a bundle path ending in a separator printed `===  ===` — Path.GetFileName of
    /// "appB/" is empty. --show-pass keeps the header, so the label is visible.
    /// </summary>
    [SkippableFact]
    public void TrailingSeparatorBundlePath_PrintsANonEmptyLabel()
    {
        TestArtifacts.SkipIfMissing();
        var (stdout, _, _) = Run(Fixture + Path.DirectorySeparatorChar, "--show-pass");
        var lines = Lines(stdout);

        Assert.DoesNotContain("===  ===", stdout);
        Assert.Contains("=== RunSummaryOnePassOneFail ===", lines);
        Assert.Contains(lines, l => l.StartsWith("[1/1] RunSummaryOnePassOneFail — ", StringComparison.Ordinal));
    }
}
