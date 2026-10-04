// JobsSharedBundleTddExcludedWorkerTests — one worker of a shared `--tdd --jobs` bundle at a time (#5262),
// the cases a two-worker run cannot pin because which worker wins a claim is a race.
//
// A process given AL_RUNNER_UNIT_CLAIM_DIR / AL_RUNNER_UNIT_CLAIM_BUNDLES is a worker as far as the claims
// are concerned, so the claim directory's earlier owner is whatever ran first: a peer that already claimed
// the dropped objects, or this process's own earlier attempt (a watchdog resume), or its own earlier pass
// (--tdd's re-run). Each is deterministic here.

using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class JobsSharedBundleTddExcludedWorkerTests
{
    private static string Fixture(string name) => ResumeRun.Fixture(name);

    /// <summary>`dependencyOnly` is what the `--jobs` parent hands a worker through AL_RUNNER_JOBS_DEPENDENCY_ONLY
    /// (#5295): always written, empty when none, so a value in this host's environment never reaches the child.</summary>
    internal static (int Exit, string Output) Worker(string claimDir, string bundle, string args, string? dependencyOnly = null, params string[] folders)
        => Worker(claimDir, bundle, args, dependencyOnly, sequentialBundles: false, folders);

    /// <summary>`sequentialBundles` is AL_RUNNER_SEQUENTIAL_BUNDLES (#4450): always written, so a value in this
    /// host's environment never reaches the child.</summary>
    internal static (int Exit, string Output) Worker(string claimDir, string bundle, string args, string? dependencyOnly,
        bool sequentialBundles, params string[] folders)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var cmd = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(root, "AlRunner")));
        cmd.Append(TestBuildConfig.BcVersionArg).Append(' ').Append(args);
        foreach (var f in folders.DefaultIfEmpty(bundle)) cmd.Append($" \"{f}\"");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = cmd.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
        };
        psi.Environment[AlRunner.Infrastructure.UnitClaimQueue.DirEnvVar] = claimDir;
        psi.Environment[AlRunner.Infrastructure.UnitClaimQueue.BundlesEnvVar] = bundle;
        psi.Environment[AlRunner.Infrastructure.JobsSourceDependencies.DependencyOnlyEnvVar] = dependencyOnly ?? "";
        psi.Environment["AL_RUNNER_SEQUENTIAL_BUNDLES"] = sequentialBundles ? "1" : "0";
        // A watchdog resume is not a crash, but a dump of this child is what CI would write for one.
        psi.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(600_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        return (p.ExitCode, stdout.GetAwaiter().GetResult() + "\n" + stderr.GetAwaiter().GetResult());
    }

    internal static string Claims(string scratch)
    {
        var dir = Path.Combine(scratch, "claims");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A peer took the dropped objects first (it claimed them while compiling, whatever it then ran), so
    /// this worker reports none of their rows, says the claimant has them, and still fails a test that reaches
    /// the dropped CODEUNIT naming it: the table of dropped codeunits is filled by every worker that loads the
    /// bundle, because a test can run on any of them. The peer, for its part, holds the rows.</summary>
    private static readonly Lazy<(string Peer, string Worker, int WorkerExit)> Followed = new(() =>
    {
        var scratch = TestScratch.Dir("al-runner-jobs-tdd-excluded-worker");
        var bundle = Fixture("JobsTddExcluded");
        var claims = Claims(scratch);
        // Control_A selects one codeunit, so the peer leaves ReachesDropped and the other codeunit to this worker.
        var (_, peer) = Worker(claims, bundle, $"--cache \"{Path.Combine(scratch, "peer-cache")}\" --tdd --test Control_A");
        var (exit, worker) = Worker(claims, bundle, $"--cache \"{Path.Combine(scratch, "cache")}\" --tdd");
        return (peer, worker, exit);
    });

    [SkippableFact]
    public void AWorkerThatLostTheClaim_ReportsNoRowsOfTheDroppedObjects_AndSaysWhoHasThem()
    {
        TestArtifacts.SkipIfMissing();
        var (peer, worker, _) = Followed.Value;

        // the peer claimed both dropped objects and reports the one with tests
        Assert.Contains("claimed 2 of 2", peer);
        Assert.Contains("FAIL  Jobs Excl Dropped.Dropped_A", peer);
        Assert.Contains("FAIL  Jobs Excl Dropped.Dropped_B", peer);

        Assert.Contains("claimed 0 of 2", worker);
        Assert.DoesNotContain("Jobs Excl Dropped.Dropped_", worker);
        Assert.Contains("2 [Test] procedure(s) of dropped objects another worker of this bundle claimed", worker);
        Assert.DoesNotContain("no test referenced a missing symbol", worker);
        // the one codeunit left to this worker ran (the peer took Control's)
        Assert.Contains("Tests: 1   passed 0   failed 1", worker);
    }

    [SkippableFact]
    public void AWorkerThatLostTheClaim_StillRegistersTheDroppedCodeunits_SoATestThatReachesOneNamesIt()
    {
        TestArtifacts.SkipIfMissing();
        var (_, worker, exit) = Followed.Value;

        Assert.Equal(1, exit);
        Assert.Contains("FAIL  \"Jobs Tdd Reaches\".ReachesDropped", worker);
        Assert.Contains("\"Jobs Tdd Helper\"", worker);
        Assert.Contains("was dropped from it because it did not compile: error AL0185", worker);
        Assert.DoesNotContain("provision", worker, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A watchdog resume is a fresh process over the same claim directory: its first attempt claimed the
    /// dropped codeunit and carries the rows, so the resumed attempt finds the claim taken and must not report
    /// them a second time; the totals are the plain resumed run's (9 tests: 5 passed, 3 failed, 1 error).</summary>
    [SkippableFact]
    public void AResumedWorker_ReportsTheClaimedRowsOnce()
    {
        TestArtifacts.SkipIfMissing();
        var scratch = TestScratch.Dir("al-runner-jobs-tdd-excluded-resume");
        var junit = Path.Combine(scratch, "run.xml");
        var (exit, output) = Worker(Claims(scratch), Fixture("ResumeEmitExcluded"),
            $"--cache \"{Path.Combine(scratch, "cache")}\" --tdd --test-timeout 3 --output-junit \"{junit}\"");

        Assert.Equal(3, exit);
        Assert.Contains("resume: a watchdog abort ended this attempt early", output);
        Assert.Contains("Tests: 9   passed 5   failed 3   errors 1", output);
        var cases = JobsSharedBundleTddExcludedTests.Cases(junit);
        var names = cases.Select(JobsSharedBundleTddExcludedTests.Name).ToList();
        Assert.Equal(9, names.Count);
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Equal(new[] { "Resume Excl Dropped.Dropped_A", "Resume Excl Dropped.Dropped_B", "Resume Excl Dropped.Dropped_C" },
            cases.Where(c => c.Elements("failure").Any()).Select(JobsSharedBundleTddExcludedTests.Name)
                .Where(n => n.StartsWith("Resume Excl Dropped.", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal));
        // the resumed attempt left the rows to the carry, and says so rather than that nothing was reported
        Assert.Contains("(or an earlier attempt of this one) are reported FAILED there, not here", output);
    }

    /// <summary>--tdd generates a member into the app folder and compiles the test bundle a second time,
    /// throwing the first pass's rows away. The worker that won the dropped codeunit in the first pass keeps it
    /// in the second: it finds its own claim file, which a plain "create" answers "exists". Two workers share
    /// the bundle, and the dropped codeunit's two rows are in the aggregate once.</summary>
    [SkippableFact]
    public void AWorkerThatCompilesTheBundleAgain_KeepsTheDroppedObjectsItClaimedInTheFirstPass()
    {
        TestArtifacts.SkipIfMissing();
        var scratch = TestScratch.Dir("al-runner-jobs-tdd-excluded-rerun");
        var junit = Path.Combine(scratch, "merged.xml");
        var (exit, output) = JobsUnitClaimEndToEndTests.RunRunner(
            $"--cache \"{Path.Combine(scratch, "cache")}\" --jobs 2 --tdd --output-junit \"{junit}\" "
            + $"\"{Fixture("JobsTddRerun/app")}\" \"{Fixture("JobsTddRerun/test")}\"", lowSplitFloor: true);

        Assert.Equal(1, exit);
        Assert.Contains("is shared by 2 worker(s)", output);
        Assert.Contains("recompiling it and compiling the bundles that depend on it again", output);
        // the generated member's test and the five Extra test codeunits' pass; the dropped codeunit's two rows fail
        Assert.Contains("Tests: 8   passed 6   failed 2   errors 0   skipped 0", output);
        var cases = JobsSharedBundleTddExcludedTests.Cases(junit);
        Assert.Equal(new[] { "Jobs Rerun Dropped.RerunDropped_A", "Jobs Rerun Dropped.RerunDropped_B" },
            cases.Where(c => c.Elements("failure").Any()).Select(JobsSharedBundleTddExcludedTests.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(6, cases.Count(c => !c.Elements("failure").Any()));
        Assert.Equal(cases.Count, cases.Select(JobsSharedBundleTddExcludedTests.Name).Distinct().Count());
    }
}
