// JobsUnitClaimEndToEndTests — `--jobs` hands one bundle's test codeunits to several workers first
// come, first served (#5130), and writes the caller's --output-junit from the shard files (#5129).
//
// Spawns real workers. The fixture's tests each sleep, so two workers that start together both get
// some of the eight codeunits; the assertions are about what must hold however the claims fall:
// every test runs exactly once, and (given the sleeps) more than one worker took part.

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace AlRunner.Tests;

public sealed class JobsUnitClaimEndToEndTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string Fixtures = Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures");

    // 8 test codeunits, 10 [Test] methods in all. The three-test one has the HIGHEST object id, so
    // object-id order and largest-first order disagree about which runs first.
    private const int ClaimFixtureTests = 10;

    internal static (int Exit, string Output) RunRunner(string extraArgs, bool lowSplitFloor, string? freeMemoryMb = null)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg).Append(' ').Append(extraArgs);
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        // The fixture is tiny; without this a bundle is never heavy enough to be shared.
        if (lowSplitFloor) psi.Environment["AL_RUNNER_JOBS_SPLIT_MIN_FILES"] = "1";
        // Pins the free-memory reading (plenty unless a test says otherwise): the plan sizes sharing
        // from it, so without this these tests would pass or fail with what the box has free.
        psi.Environment[AlRunner.Infrastructure.JobsMemory.FreeMemoryEnvVar] = freeMemoryMb ?? "1000000";
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(600_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (p.ExitCode, sb.ToString());
    }

    internal static List<int> ShardTestCounts(string output) =>
        Regex.Matches(output, @"─+ shard \d+ \(exit [^)]*\) ─+[\s\S]*?^Tests: (\d+)", RegexOptions.Multiline)
            .Select(m => int.Parse(m.Groups[1].Value)).ToList();

    private static string[] ClaimedRunOrder(string scratch, string claimDir)
    {
        var bundle = Path.Combine(Fixtures, "JobsUnitClaim");
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg)
            .Append($" --cache \"{Path.Combine(scratch, "cache")}\" --output-json \"{bundle}\"");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        psi.Environment[UnitClaimQueueEnv.Dir] = claimDir;
        psi.Environment[UnitClaimQueueEnv.Bundles] = bundle;
        var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(600_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        var json = stdout.GetAwaiter().GetResult();
        Assert.True(p.ExitCode == 0, $"exit {p.ExitCode}\n{stderr.GetAwaiter().GetResult()}\n{json}");
        return System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("tests").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!).ToArray();
    }

    private static class UnitClaimQueueEnv
    {
        public const string Dir = AlRunner.Infrastructure.UnitClaimQueue.DirEnvVar;
        public const string Bundles = AlRunner.Infrastructure.UnitClaimQueue.BundlesEnvVar;
    }

    /// <summary>One worker's view of the claim: it takes the largest codeunit FIRST (the claim
    /// order, which the three-test codeunit with the highest object id would lose under object-id
    /// order), runs everything once, and a second process on the same claim directory finds every
    /// codeunit taken and runs nothing.</summary>
    [SkippableFact]
    public void AWorker_ClaimsLargestFirst_AndASecondProcessOnTheSameClaimsRunsNothing()
    {
        TestArtifacts.SkipIfMissing();
        var scratch = TestScratch.Dir("al-runner-jobs-unit-claim-order");
        var claims = Path.Combine(scratch, "claims");
        Directory.CreateDirectory(claims);

        var first = ClaimedRunOrder(scratch, claims);

        Assert.Equal(ClaimFixtureTests, first.Length);
        Assert.StartsWith("Codeunit50968.", first[0]);
        Assert.Equal(3, first.Count(n => n.StartsWith("Codeunit50968.", StringComparison.Ordinal)));
        Assert.Equal(first.Length, first.Distinct().Count());

        Assert.Empty(ClaimedRunOrder(scratch, claims));
    }

    [SkippableFact]
    public void OneBundle_UnderJobs_IsSharedByTheWorkers_AndEveryTestRunsExactlyOnce()
    {
        TestArtifacts.SkipIfMissing();
        var scratch = TestScratch.Dir("al-runner-jobs-unit-claim");
        var junit = Path.Combine(scratch, "merged.xml");

        var (exit, output) = RunRunner(
            $"--cache \"{Path.Combine(scratch, "cache")}\" --jobs 2 --output-junit \"{junit}\" "
            + $"\"{Path.Combine(Fixtures, "JobsUnitClaim")}\"", lowSplitFloor: true);

        Assert.True(exit == 0, $"expected exit 0, got {exit}.\n{output}");
        Assert.Contains("is shared by 2 worker(s)", output);

        // Each test once: a claim that handed one codeunit to both workers would double it, and a
        // worker that dropped a claimed one would shrink it.
        Assert.Contains($"Tests: {ClaimFixtureTests}   passed {ClaimFixtureTests}", output);
        var shardCounts = ShardTestCounts(output);
        Assert.Equal(2, shardCounts.Count);
        Assert.Equal(ClaimFixtureTests, shardCounts.Sum());
        // and it really was shared: the tests sleep, so a worker that started with the other
        // gets a share
        Assert.All(shardCounts, c => Assert.True(c > 0, $"a worker ran nothing: {string.Join("/", shardCounts)}"));

        // the merged report the caller asked for holds each case once
        Assert.True(File.Exists(junit), $"--output-junit was not written.\n{output}");
        var cases = XDocument.Load(junit).Descendants("testcase")
            .Select(e => $"{e.Attribute("classname")!.Value}.{e.Attribute("name")!.Value}").ToList();
        Assert.Equal(ClaimFixtureTests, cases.Count);
        Assert.Equal(cases.Count, cases.Distinct().Count());
    }

    /// <summary>Disabled isolation keeps state across every test, so the bundle is never shared: one
    /// bundle then runs in one process exactly as it did before, and runs all its tests.</summary>
    [SkippableFact]
    public void DisabledIsolation_NeverSharesTheBundle()
    {
        TestArtifacts.SkipIfMissing();
        var scratch = TestScratch.Dir("al-runner-jobs-unit-claim-disabled");

        var (exit, output) = RunRunner(
            $"--cache \"{Path.Combine(scratch, "cache")}\" --jobs 2 --isolation disabled "
            + $"\"{Path.Combine(Fixtures, "JobsUnitClaim")}\"", lowSplitFloor: true);

        Assert.True(exit == 0, $"expected exit 0, got {exit}.\n{output}");
        Assert.DoesNotContain("is shared by", output);
        Assert.DoesNotContain("worker process(es)", output);
        Assert.Contains($"Tests: {ClaimFixtureTests}   passed {ClaimFixtureTests}", output);
    }

    /// <summary>A bundle that is not heavy enough stays whole: the floor is what keeps `--jobs 8` on
    /// a small bundle from paying eight startups.</summary>
    [SkippableFact]
    public void ALightBundle_IsNotShared()
    {
        TestArtifacts.SkipIfMissing();
        var scratch = TestScratch.Dir("al-runner-jobs-unit-claim-light");

        var (exit, output) = RunRunner(
            $"--cache \"{Path.Combine(scratch, "cache")}\" --jobs 2 \"{Path.Combine(Fixtures, "JobsUnitClaim")}\"",
            lowSplitFloor: false);

        Assert.True(exit == 0, $"expected exit 0, got {exit}.\n{output}");
        Assert.DoesNotContain("is shared by", output);
        Assert.Contains($"Tests: {ClaimFixtureTests}   passed {ClaimFixtureTests}", output);
    }

    /// <summary>One bundle never fanned out before, so it must not start refusing a flag a fan-out refuses
    /// (`--count-out`) or losing a report one cannot write: with `--count-out` the bundle stays in one process.
    /// `--out` and `--output-json` are written by the parent from the workers' results (#5129), so a shared
    /// bundle is fanned out with them (JobsReportsEndToEndTests).</summary>
    [SkippableFact]
    public void AFlagAFanOutCannotHonour_KeepsOneBundleInOneProcess()
    {
        TestArtifacts.SkipIfMissing();
        const string flag = "--count-out";
        var scratch = TestScratch.Dir("al-runner-jobs-unit-claim-flagcountout");
        var target = Path.Combine(scratch, "report.json");

        var (exit, output) = RunRunner(
            $"--cache \"{Path.Combine(scratch, "cache")}\" --jobs 2 {flag} \"{target}\" \"{Path.Combine(Fixtures, "JobsUnitClaim")}\"",
            lowSplitFloor: true);

        Assert.True(exit == 0, $"expected exit 0, got {exit}.\n{output}");
        Assert.DoesNotContain("is shared by", output);
        Assert.DoesNotContain("worker process(es)", output);
        Assert.True(File.Exists(target), $"{flag} wrote nothing.\n{output}");
    }

    /// <summary>#5129 item 1: with several bundles the parent used to hand each worker a private
    /// JUnit path and never write the caller's. It must exist, and hold every worker's tests.</summary>
    [SkippableFact]
    public void SeveralBundles_UnderJobs_WriteTheCallersJUnit()
    {
        TestArtifacts.SkipIfMissing();
        var scratch = TestScratch.Dir("al-runner-jobs-merged-junit");
        var junit = Path.Combine(scratch, "out", "junit.xml");

        var (exit, output) = RunRunner(
            $"--cache \"{Path.Combine(scratch, "cache")}\" --jobs 2 --output-junit \"{junit}\" "
            + $"\"{Path.Combine(Fixtures, "RecordTriggerXRec")}\" \"{Path.Combine(Fixtures, "CoverageBranch")}\"",
            lowSplitFloor: false);

        Assert.True(File.Exists(junit), $"--output-junit was not written.\n{output}");
        // the LAST counts line is the parent's aggregate; each shard section printed its own above
        var aggregate = Regex.Matches(output, @"^Tests: (\d+)   passed", RegexOptions.Multiline).LastOrDefault();
        Assert.True(aggregate is { Success: true }, output);
        var root = XDocument.Load(junit).Root!;
        Assert.Equal(aggregate.Groups[1].Value, root.Attribute("tests")!.Value);
        Assert.Equal(aggregate.Groups[1].Value, root.Descendants("testcase").Count().ToString());
        Assert.True(int.Parse(aggregate.Groups[1].Value) > 0, output);
        _ = exit;   // CoverageBranch carries one deliberate failure, so the exit code is not the claim here
    }

    /// <summary>The refusal is on the path most `--jobs` runs take, several bundles, where the
    /// split decision is made inside the fan-out rather than at the gate. `--isolation disabled`
    /// keeps state across every test, so no bundle may be shared, and the plan line says why.</summary>
    [SkippableFact]
    public void SeveralBundles_WithDisabledIsolation_AreNotSplit_AndSayWhy()
    {
        TestArtifacts.SkipIfMissing();
        var scratch = TestScratch.Dir("al-runner-jobs-unit-claim-multi-disabled");

        var (exit, output) = RunRunner(
            $"--cache \"{Path.Combine(scratch, "cache")}\" --jobs 3 --isolation disabled "
            + $"\"{Path.Combine(Fixtures, "JobsUnitClaim")}\" \"{Path.Combine(Fixtures, "RecordTriggerXRec")}\"",
            lowSplitFloor: true);

        Assert.Contains("jobs: not splitting a bundle across workers: --isolation disabled", output);
        Assert.DoesNotContain("is shared by", output);
        var aggregate = Regex.Matches(output, @"^Tests: (\d+)   passed", RegexOptions.Multiline).LastOrDefault();
        Assert.True(aggregate is { Success: true }, output);
        Assert.True(int.Parse(aggregate!.Groups[1].Value) >= ClaimFixtureTests, output);
        _ = exit;
    }

    /// <summary>A shared bundle that does not compile prints its COMPILE FAIL header once per worker.
    /// It is one missing bundle, and the aggregate must say one, not two.</summary>
    [SkippableFact]
    public void ASharedBundleThatDoesNotCompile_IsCountedOnceAsNotRun()
    {
        TestArtifacts.SkipIfMissing();
        var scratch = TestScratch.Dir("al-runner-jobs-unit-claim-broken");

        var (exit, output) = RunRunner(
            $"--cache \"{Path.Combine(scratch, "cache")}\" --jobs 2 \"{Path.Combine(Fixtures, "JobsUnitClaimBroken")}\"",
            lowSplitFloor: true);

        Assert.Equal(3, exit);
        Assert.Contains("is shared by 2 worker(s)", output);
        Assert.Equal(2, Regex.Matches(output, "JobsUnitClaimBroken — COMPILE FAIL ===").Count);
        Assert.Contains("NOT RUN:     1 bundle(s)", output);
    }

    /// <summary>The sibling shape: a shared bundle that compiled and lost suites reports SUITE
    /// ERRORS from every worker, and is one partial bundle.</summary>
    [SkippableFact]
    public void ASharedBundleThatLostASuite_IsCountedOnceAsPartial()
    {
        TestArtifacts.SkipIfMissing();
        var scratch = TestScratch.Dir("al-runner-jobs-unit-claim-partial");

        var (exit, output) = RunRunner(
            $"--cache \"{Path.Combine(scratch, "cache")}\" --jobs 2 \"{Path.Combine(Fixtures, "JobsUnitClaimPartial")}\"",
            lowSplitFloor: true);

        Assert.Equal(3, exit);
        Assert.Equal(2, Regex.Matches(output, "JobsUnitClaimPartial — SUITE ERRORS").Count);
        Assert.Contains("PARTIAL:     1 bundle(s)", output);
    }
}
