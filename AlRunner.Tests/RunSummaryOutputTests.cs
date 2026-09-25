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

    // Facts asserting on the same invocation share one spawn: each spawn costs ~10 s, and the
    // class is serial, so a spawn per fact is what made it the #1887 tail.
    private static readonly Lazy<(string Stdout, string Stderr, int Exit)> DefaultRun =
        new(() => Run(Fixture));
    private static readonly Lazy<(string Stdout, string Stderr, int Exit)> ShowPassTrailingSeparatorRun =
        new(() => Run(Fixture + Path.DirectorySeparatorChar, "--show-pass"));

    /// <summary>
    /// #4563 (owner decision 2026-09-25): PASS lines are hidden by default; the failure entry
    /// and the counts still say what passed.
    /// </summary>
    [SkippableFact]
    public void DefaultRun_ListsTheFailure_NotThePass()
    {
        TestArtifacts.SkipIfMissing();
        var (stdout, stderr, exit) = DefaultRun.Value;

        Assert.True(exit == 1, $"one test fails by construction, so exit 1:\n{stdout}\n{stderr}");
        Assert.DoesNotContain(Lines(stdout), l => l.StartsWith("PASS ", StringComparison.Ordinal));
        Assert.True(RunnerFailureLines.Failed(stdout, 50150, "CustomerNameFails"), stdout);
        Assert.Contains("Tests: 2   passed 1   failed 1   errors 0 ", stdout);
    }

    /// <summary>
    /// #4563's other half: --show-pass brings the PASS lines back. Shares the trailing-separator
    /// run below; the separator does not bear on which lines print.
    /// </summary>
    [SkippableFact]
    public void ShowPass_ListsThePassingTest()
    {
        TestArtifacts.SkipIfMissing();
        var (stdout, _, _) = ShowPassTrailingSeparatorRun.Value;

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
        var (stdout, _, _) = DefaultRun.Value;

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
        var (stdout, _, exit) = DefaultRun.Value;
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

    private static string LastResultLine(string stdout) =>
        Assert.Single(Lines(stdout), l => l.StartsWith("Result:", StringComparison.Ordinal));

    /// <summary>
    /// #4562: the Result line states the code the process exits with AFTER every escalation —
    /// here an --output-junit path that cannot be written turns an all-passing run's 0 into 2.
    /// </summary>
    [SkippableFact]
    public void ResultLine_StatesTheEscalatedExitCode()
    {
        TestArtifacts.SkipIfMissing();
        // A directory where the file should go: the write fails after every test passed.
        var junitDir = TestScratch.Dir("al-runner-run-summary-junit-is-a-dir");
        Directory.CreateDirectory(junitDir);
        var (stdout, stderr, exit) = Run(Fixture,
            "--test", "Codeunit50150.CustomerInsertPasses", "--output-junit", $"\"{junitDir}\"");

        Assert.True(exit == 2, $"the passing test alone, then a lost output file:\n{stdout}\n{stderr}");
        Assert.StartsWith("Result: FAILED, exit code 2 (", LastResultLine(stdout));
    }

    /// <summary>
    /// #4563: --quiet (and --failures-only) wins over --verbose for PASS lines, whatever order
    /// they come in; the failure is still listed.
    /// </summary>
    [SkippableFact]
    public void Quiet_HidesPassLines_EvenWithVerbose()
    {
        TestArtifacts.SkipIfMissing();
        var (stdout, _, _) = Run(Fixture, "--verbose", "--quiet");

        Assert.DoesNotContain(Lines(stdout), l => l.StartsWith("PASS ", StringComparison.Ordinal));
        Assert.True(RunnerFailureLines.Failed(stdout, 50150, "CustomerNameFails"), stdout);
    }

    /// <summary>
    /// #4562 under --jobs: the one line starting `Result:` is the parent's, and it states the
    /// process exit — a shard's own verdict must not be the last `Result:` a reader finds.
    /// </summary>
    [SkippableFact]
    public void Jobs_PrintOneResultLine_ThatMatchesTheProcessExit()
    {
        TestArtifacts.SkipIfMissing();
        // A copy of the fixture that cannot compile, beside the original that runs and fails.
        var broken = Path.Combine(TestScratch.Dir("al-runner-run-summary-jobs"), "BrokenCopy");
        if (Directory.Exists(broken)) Directory.Delete(broken, recursive: true);
        Directory.CreateDirectory(broken);
        foreach (var f in Directory.GetFiles(Fixture))
            File.Copy(f, Path.Combine(broken, Path.GetFileName(f)));
        File.AppendAllText(Path.Combine(broken, "ProbeCustomerTest.Codeunit.al"), "\nthis does not compile\n");

        var (stdout, stderr, exit) = Run(Fixture, "--jobs", "2", $"\"{broken}\"");

        Assert.True(exit == 3, $"one shard cannot compile, so the run exits 3:\n{stdout}\n{stderr}");
        Assert.Equal("Result: FAILED, exit code 3 (an app could not compile)", LastResultLine(stdout));
        Assert.Equal("Result: FAILED, exit code 3 (an app could not compile)",
            Lines(stdout.TrimEnd('\n'))[^1]);
        // Each shard still says what it saw, labelled as a shard.
        Assert.Contains("Shard result: FAILED, exit code 1 (", stdout);
        Assert.Contains("Shard result: FAILED, exit code 3 (", stdout);
    }

    /// <summary>
    /// #4562's bug: a bundle path ending in a separator printed `===  ===` — Path.GetFileName of
    /// "appB/" is empty. --show-pass keeps the header, so the label is visible.
    /// </summary>
    [SkippableFact]
    public void TrailingSeparatorBundlePath_PrintsANonEmptyLabel()
    {
        TestArtifacts.SkipIfMissing();
        var (stdout, _, _) = ShowPassTrailingSeparatorRun.Value;
        var lines = Lines(stdout);

        Assert.DoesNotContain("===  ===", stdout);
        Assert.Contains("=== RunSummaryOnePassOneFail ===", lines);
        Assert.Contains(lines, l => l.StartsWith("[1/1] RunSummaryOnePassOneFail — ", StringComparison.Ordinal));
    }
}
