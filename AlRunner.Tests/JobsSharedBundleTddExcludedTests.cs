// JobsSharedBundleTddExcludedTests — `--tdd --jobs` on a bundle the workers share (#5262). Every worker
// compiles the bundle and finds the same TDD-EXCLUDED objects, and each used to report their synthetic FAILED
// rows, so the aggregate counted them once per worker (12 tests with 6 failed where one process counts 9 with 3).
//
// The objects are claimed through the same per-object claim as EMIT-EXCLUDED's SKIPPED rows (#5256), so the
// worker that wins an object reports its rows and a peer says so in its own output. Which worker wins is the
// race to the claim directory, so every assertion about a shard holds for either assignment. One run of each
// layout serves every test of it: the Lazy is the only subprocess.

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace AlRunner.Tests;

public sealed class JobsSharedBundleTddExcludedTests
{
    private static readonly string Fixtures = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")), "AlRunner.Tests", "Fixtures");

    private static string Fixture(string name) => Path.Combine(Fixtures, name);

    // JobsUnitClaimExcluded: 3 healthy codeunits of 2 tests, and one dropped codeunit of 3.
    private const string ExcludedCounts = "Tests: 9   passed 6   failed 3   errors 0";
    private static readonly string[] DroppedRows = { "Dropped_A", "Dropped_B", "Dropped_C" };

    /// <summary>A `--tdd --jobs` run with the two streams kept apart. The parent prints each shard's stdout (after
    /// its header) and then its stderr, but the two pipes are read on separate threads, so a combined text does not
    /// keep that order: attributing a stderr line to a shard needs stderr on its own, and `Output` is for the
    /// assertions that do not.</summary>
    internal sealed record JobsRun(int Exit, string Stdout, string Stderr, string Junit)
    {
        public string Output => Stdout + "\n" + Stderr;
    }

    private static JobsRun Jobs(string scratchName, int jobs, params string[] folders)
    {
        var scratch = TestScratch.Dir(scratchName);
        var junit = Path.Combine(scratch, "merged.xml");
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var args = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(root, "AlRunner")));
        args.Append(TestBuildConfig.BcVersionArg)
            .Append($" --cache \"{Path.Combine(scratch, "cache")}\" --jobs {jobs} --tdd --output-junit \"{junit}\" ")
            .Append(string.Join(" ", folders.Select(f => $"\"{Fixture(f)}\"")));
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
        };
        // The fixtures are tiny; without this a bundle is never heavy enough to be shared, and the plan sizes
        // sharing from the free memory it reads, so that is pinned too (JobsUnitClaimEndToEndTests.RunRunner).
        psi.Environment["AL_RUNNER_JOBS_SPLIT_MIN_FILES"] = "1";
        psi.Environment[AlRunner.Infrastructure.JobsMemory.FreeMemoryEnvVar] = "1000000";
        var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(600_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        return new JobsRun(p.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult(), junit);
    }

    private static readonly Lazy<JobsRun> Shared =
        new(() => Jobs("al-runner-jobs-shared-tdd-excluded", 2, "JobsUnitClaimExcluded"));

    /// <summary>The oracle: the same folder, one process.</summary>
    private static readonly Lazy<(int Exit, string Output, string Junit)> Plain = new(() =>
    {
        var scratch = TestScratch.Dir("al-runner-jobs-shared-tdd-excluded-plain");
        var junit = Path.Combine(scratch, "plain.xml");
        var (exit, stdout, stderr) = ResumeRun.Runner(
            $"--cache \"{Path.Combine(scratch, "cache")}\" --tdd --output-junit \"{junit}\" \"{Fixture("JobsUnitClaimExcluded")}\"");
        return (exit, stdout + "\n" + stderr, junit);
    });

    /// <summary>The control: nothing dropped, so no claim is made and every worker reports as it did.</summary>
    private static readonly Lazy<JobsRun> NothingDropped =
        new(() => Jobs("al-runner-jobs-shared-tdd-nothing-dropped", 2, "JobsUnitClaim"));

    /// <summary>Two bundles that each drop a codeunit named "Jobs Excl Dropped", at two paths, each shared by
    /// two of four workers.</summary>
    private static readonly Lazy<JobsRun> TwoBundles =
        new(() => Jobs("al-runner-jobs-shared-tdd-two-bundles", 4, "JobsUnitClaimExcluded", "JobsTddExcluded"));

    internal static List<string> Shards(string output)
        => Regex.Split(output, @"^─+ shard \d+ \(exit [^)]*\) ─+$", RegexOptions.Multiline).Skip(1).ToList();

    internal static List<XElement> Cases(string junit)
    {
        Assert.True(File.Exists(junit), $"--output-junit was not written: {junit}");
        return XDocument.Load(junit).Descendants("testcase").ToList();
    }

    internal static string Name(XElement c) => $"{c.Attribute("classname")!.Value}.{c.Attribute("name")!.Value}";

    /// <summary>The aggregate counts the dropped codeunit's three FAILED tests once, as a single process
    /// does: 9 tests with 3 failed, not 12 with 6.</summary>
    [SkippableFact]
    public void TheDroppedCodeunitsFailedTests_AreCountedOnceInTheAggregate_AsOneProcessCountsThem()
    {
        TestArtifacts.SkipIfMissing();
        var shared = Shared.Value;
        var (exit, output) = (shared.Exit, shared.Output);
        var plain = Plain.Value;

        Assert.Contains("is shared by 2 worker(s)", output);
        Assert.Contains(ExcludedCounts, plain.Output);
        // one process claims nothing and says what it always said
        Assert.Contains("3 [Test] procedure(s) they declare report as FAILED instead of vanishing from the run.", plain.Output);
        Assert.DoesNotContain("shared out", plain.Output);
        Assert.DoesNotContain("not here", plain.Output);
        Assert.Contains($"{ExcludedCounts}   skipped 0", output);
        Assert.Equal(1, plain.Exit);
        Assert.Equal(plain.Exit, exit);
        Assert.Equal(9, JobsUnitClaimEndToEndTests.ShardTestCounts(output).Sum());
    }

    /// <summary>The report the caller asked for holds each case once, and is the plain run's: the same
    /// names, the same three failed.</summary>
    [SkippableFact]
    public void TheMergedJUnit_IsThePlainRunsJUnit()
    {
        TestArtifacts.SkipIfMissing();
        var shared = Cases(Shared.Value.Junit);
        var plain = Cases(Plain.Value.Junit);

        Assert.Equal(9, shared.Count);
        Assert.Equal(plain.Select(Name).OrderBy(n => n, StringComparer.Ordinal), shared.Select(Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(DroppedRows, shared.Where(c => c.Elements("failure").Any()).Select(c => c.Attribute("name")!.Value)
            .OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>Exactly one worker owns the dropped object: its output holds the three FAILED rows and says it
    /// claimed the object; the other prints none of them, says so, and does not claim that no test referenced a
    /// missing symbol. Both still name the drop (the TDD-EXCLUDED line is every worker's own compile).</summary>
    [SkippableFact]
    public void OneWorkerReportsTheRows_AndThePeerSaysItLeftThemToTheClaimant()
    {
        TestArtifacts.SkipIfMissing();
        var (_, stdout, stderr, _) = Shared.Value;
        var shards = Shards(stdout);
        // each worker prints one TDD-EXCLUDED line (its own compile), and the parent prints a shard's stderr in shard order
        var tddLines = Regex.Matches(stderr, @"^<bundled>: TDD-EXCLUDED — .*$", RegexOptions.Multiline).Select(m => m.Value).ToList();

        Assert.Equal(2, shards.Count);
        Assert.Equal(2, tddLines.Count);
        var ownerIndex = shards.FindIndex(s => s.Contains("FAIL  Jobs Excl Dropped.Dropped_A"));
        Assert.True(ownerIndex >= 0, stdout);
        var peerIndex = 1 - ownerIndex;
        foreach (var row in DroppedRows)
        {
            Assert.Contains($"FAIL  Jobs Excl Dropped.{row}", shards[ownerIndex]);
            Assert.DoesNotContain($"Jobs Excl Dropped.{row}", shards[peerIndex]);
        }
        Assert.Contains("claimed 1 of 1", tddLines[ownerIndex]);
        Assert.Contains("claimed 0 of 1", tddLines[peerIndex]);
        Assert.Contains("every missing symbol was reported as a failed test instead (see the FAILED test messages above", shards[ownerIndex]);
        Assert.DoesNotContain("reported FAILED there, not here", shards[ownerIndex]);
        Assert.Contains("3 [Test] procedure(s) of dropped objects another worker of this bundle claimed", shards[peerIndex]);
        foreach (var shard in shards)
            Assert.DoesNotContain("no test referenced a missing symbol", shard);
    }

    /// <summary>The control that must not change: no dropped object, so no claim and no peer note. Every worker
    /// still says what a --tdd run that generated nothing says, and the aggregate and the report hold each test
    /// once.</summary>
    [SkippableFact]
    public void ARunWithNothingDropped_IsUnchanged()
    {
        TestArtifacts.SkipIfMissing();
        var nothing = NothingDropped.Value;
        var (exit, output, junit) = (nothing.Exit, nothing.Output, nothing.Junit);

        Assert.Equal(0, exit);
        Assert.Contains("is shared by 2 worker(s)", output);
        Assert.Contains("Tests: 10   passed 10   failed 0   errors 0   skipped 0", output);
        Assert.DoesNotContain("TDD-EXCLUDED", output);
        Assert.DoesNotContain("not here", output);
        Assert.Equal(2, Regex.Matches(output, "no test referenced a missing symbol").Count);
        var names = Cases(junit).Select(Name).ToList();
        Assert.Equal(10, names.Count);
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    /// <summary>Two shared bundles each drop a codeunit of the same name at a different path: each is claimed
    /// for its own bundle, so each bundle's rows are counted once (3 and 2 FAILED, never one bundle's claim
    /// hiding the other's).</summary>
    [SkippableFact]
    public void SameNamedDroppedObjectsInTwoSharedBundles_AreEachReportedOnce()
    {
        TestArtifacts.SkipIfMissing();
        var two = TwoBundles.Value;
        var (exit, output, junit) = (two.Exit, two.Output, two.Junit);

        Assert.Equal(1, exit);
        Assert.Equal(2, Regex.Matches(output, @"is shared by 2 worker\(s\)").Count);
        // JobsUnitClaimExcluded: 6 passed, 3 failed. JobsTddExcluded: Control_A/B and ReachesDropped's
        // neighbours pass, ReachesDropped and the two Dropped rows fail: 2 passed, 3 failed.
        Assert.Contains("Tests: 14   passed 8   failed 6   errors 0   skipped 0", output);
        var failed = Cases(junit).Where(c => c.Elements("failure").Any()).Select(Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(new[]
        {
            "Jobs Excl Dropped.Dropped_A", "Jobs Excl Dropped.Dropped_A", "Jobs Excl Dropped.Dropped_B",
            "Jobs Excl Dropped.Dropped_B", "Jobs Excl Dropped.Dropped_C", "Codeunit51002.ReachesDropped",
        }.OrderBy(n => n, StringComparer.Ordinal), failed);
    }
}
