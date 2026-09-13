using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #2502: every run has a run seed, each [Test] starts from <c>new Random(RunSeed.Derive(...))</c>,
/// <c>Randomize(seed)</c> is honored and <c>Randomize()</c> is reseeded from the test's derived
/// seed. A runner feature, not a BC-behaviour claim, so it is proven here rather than upstream.
/// </summary>
public class RunSeedTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private const int CodeunitId = 62502;
    private const int Max = 1000000;

    // Golden values: a change here silently invalidates every seed a user has recorded.
    [Theory]
    [InlineData(4271833, 50100, "PostingRoundsAmountCorrectly", -1379714443)]
    [InlineData(4271833, 50100, "PostingRoundsAmountCorrectlY", -842830635)]
    [InlineData(4271834, 50100, "PostingRoundsAmountCorrectly", -1936696938)]
    [InlineData(4271833, 50101, "PostingRoundsAmountCorrectly", 1186558372)]
    [InlineData(0, 0, "", -1742010779)]
    public void Derive_IsFnv1aOverRunSeedCodeunitAndMethod(int runSeed, int codeunitId, string method, int expected)
        => Assert.Equal(expected, RunSeed.Derive(runSeed, codeunitId, method));

    [Theory]
    [InlineData("4271833", true, 4271833)]
    [InlineData(" -17 ", true, -17)]
    [InlineData("12a", false, 0)]
    [InlineData("", false, 0)]
    [InlineData("99999999999", false, 0)]
    public void TryParse_AcceptsOnlyWholeInt32(string raw, bool ok, int expected)
    {
        Assert.Equal(ok, RunSeed.TryParse(raw, out var seed));
        if (ok) Assert.Equal(expected, seed);
    }

    [SkippableFact]
    public void Seed_ReproducesRandomPerTest_InSeparateProcesses_AndIsolatedRerun()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = WriteFixture();
        var junit = Path.Combine(TestScratch.Dir("al-runner-run-seed-2502-junit"), "r.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(junit)!);

        const int seed = 4271833;
        var full = RunRunner($"--seed {seed} --output-junit \"{junit}\" \"{bundle}\"");
        var alone = RunRunner($"--seed {seed} --filter B_Plain \"{bundle}\"");
        var unseeded = RunRunner($"\"{bundle}\"");

        // Seeded run reports its seed on the console and in JUnit.
        Assert.Contains($"seed: {seed}", full.Output);
        Assert.Contains($"<property name=\"seed\" value=\"{seed}\"", File.ReadAllText(junit));

        // Each plain test starts on exactly its derived sequence.
        var a = Values(full.Output, "A_Plain");
        var b = Values(full.Output, "B_Plain");
        Assert.Equal(Expected(RunSeed.Derive(seed, CodeunitId, "A_Plain")), a);
        Assert.Equal(Expected(RunSeed.Derive(seed, CodeunitId, "B_Plain")), b);
        Assert.NotEqual(a, b);

        // B alone, in a second process, sees what B saw in the full suite.
        Assert.Equal(b, Values(alone.Output, "B_Plain"));
        Assert.DoesNotContain("A_Plain=values", alone.Output);

        // Randomize(42) is honored: BC's own seeded sequence, whatever the run seed.
        Assert.Equal(Expected(42), Values(full.Output, "C_RandomizeSeeded"));
        Assert.Equal(Expected(42), Values(unseeded.Output, "C_RandomizeSeeded"));

        // Randomize() reseeds from the test's derived seed and warns.
        Assert.Equal(Expected(RunSeed.Derive(seed, CodeunitId, "D_RandomizeNoSeed")),
            Values(full.Output, "D_RandomizeNoSeed"));
        Assert.Contains($"Codeunit{CodeunitId}.D_RandomizeNoSeed called Randomize() without a seed", full.Output);

        // No --seed: a generated seed is printed, and it is the one the tests used.
        var printed = Regex.Match(unseeded.Output, @"^seed: (-?\d+)\r?$", RegexOptions.Multiline);
        Assert.True(printed.Success, "no `seed: N` line without --seed:\n" + unseeded.Output);
        var generated = int.Parse(printed.Groups[1].Value);
        Assert.NotEqual(seed, generated);
        Assert.Equal(Expected(RunSeed.Derive(generated, CodeunitId, "A_Plain")), Values(unseeded.Output, "A_Plain"));
        Assert.NotEqual(a, Values(unseeded.Output, "A_Plain"));
    }

    /// <summary>
    /// --jobs workers are separate processes: each must use the parent's run seed, not generate
    /// its own, or the one printed seed reproduces nothing.
    /// </summary>
    [SkippableFact]
    public void Jobs_WorkersShareOneGeneratedRunSeed()
    {
        TestArtifacts.SkipIfMissing();
        const int otherId = 62503;
        var r = RunRunner($"--jobs 2 \"{WriteFixture()}\" \"{WriteFixture(otherId)}\"");

        var seeds = Regex.Matches(r.Output, @"^seed: (-?\d+)\r?$", RegexOptions.Multiline)
            .Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.True(seeds.Count == 2, $"expected one seed line per worker, got {seeds.Count}:\n{r.Output}");
        Assert.Single(seeds.Distinct());
        var reported = Regex.Matches(r.Output, @"A_Plain=values:(\d+),(\d+),(\d+)")
            .Select(m => string.Join(",", m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value))
            .Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        var expected = new[] { CodeunitId, otherId }
            .Select(id => string.Join(",", Expected(RunSeed.Derive(seeds[0], id, "A_Plain"))))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, reported);
    }

    [Fact]
    public void Seed_NotAWholeNumber_ExitsTwo()
    {
        var r = RunRunner($"--seed nope \"{WriteFixture()}\"");
        Assert.Equal(2, r.Exit);
        Assert.Contains("--seed: 'nope' is not a whole number.", r.Output);
    }

    private static int[] Expected(int seed)
    {
        var rng = new Random(seed);
        return new[] { rng.Next(Max) + 1, rng.Next(Max) + 1, rng.Next(Max) + 1 };
    }

    private static int[] Values(string output, string method)
    {
        var m = Regex.Match(output, method + @"=values:(\d+),(\d+),(\d+)");
        Assert.True(m.Success, $"{method} did not report its values:\n{output}");
        return new[] { int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value) };
    }

    private static string WriteFixture(int codeunitId = CodeunitId)
    {
        var root = TestScratch.Dir("al-runner-run-seed-2502");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "app.json"), $$"""
        {
          "id": "b2502000-0000-4000-8000-0000000{{codeunitId}}",
          "name": "RunSeed{{codeunitId}}",
          "publisher": "Repro2502",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{codeunitId}}, "to": {{codeunitId}} } ],
          "runtime": "14.0"
        }
        """);
        // Each test fails on purpose: its Error() text is how the drawn values reach the output.
        File.WriteAllText(Path.Combine(root, "RunSeedProbe.al"), $$"""
        codeunit {{codeunitId}} "Run Seed Probe {{codeunitId}}"
        {
            Subtype = Test;

            [Test]
            procedure A_Plain()
            begin
                Error('A_Plain=values:' + Draw());
            end;

            [Test]
            procedure B_Plain()
            begin
                Error('B_Plain=values:' + Draw());
            end;

            [Test]
            procedure C_RandomizeSeeded()
            begin
                Randomize(42);
                Error('C_RandomizeSeeded=values:' + Draw());
            end;

            [Test]
            procedure D_RandomizeNoSeed()
            var
                Discarded: Integer;
            begin
                // Draw first, so a Randomize() that did nothing would leave a different sequence.
                Discarded := Random(1000);
                Randomize();
                Error('D_RandomizeNoSeed=values:' + Draw());
            end;

            local procedure Draw(): Text
            begin
                exit(Format(Random(1000000), 0, 9) + ',' + Format(Random(1000000), 0, 9) + ',' + Format(Random(1000000), 0, 9));
            end;
        }
        """);
        return root;
    }

    private static (string Output, int Exit) RunRunner(string extraArgs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg + " " + extraArgs,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        // An inherited AL_RUNNER_SEED would stand in for the generated seed this test checks.
        psi.Environment.Remove(RunSeed.EnvVar);
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(240_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }
}
