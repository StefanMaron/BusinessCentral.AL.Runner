// JobsTddDependencyOnlyTests — a `--tdd --jobs` worker that RUNS one bundle and only COMPILES a
// dependency-only folder (#5318). --tdd generates a member into the dependency-only folder and compiles the
// cycle again (#5037); a worker that ran its bundle in the first pass had claimed its test codeunits
// (UnitClaimQueue), so the second pass skipped them and nobody reported them.
//
// One process plays the worker, with the environment a `--jobs` parent hands it. The oracle is the same two
// folders in one process. Fixtures/JobsTddRerun: `app` (a codeunit with no members) and `test` (a test calling a
// member `app` lacks, five test codeunits of one test each, and a dropped test codeunit of two tests).
// The end-to-end `--jobs 2` run of the same fixture is in JobsSharedBundleTddExcludedWorkerTests.

using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class JobsTddDependencyOnlyTests
{
    private static string Fixture(string name) => ResumeRun.Fixture(name);

    private const string Recompiling = "recompiling it and compiling the bundles that depend on it again";

    private sealed record Run(int Exit, string Output, string Junit);

    private static Run Worker(string scratchName, string bundle, string flags, string? dependencyOnly, params string[] folders)
    {
        var scratch = TestScratch.Dir(scratchName);
        var junit = Path.Combine(scratch, "run.xml");
        var (exit, output) = JobsSharedBundleTddExcludedWorkerTests.Worker(
            JobsSharedBundleTddExcludedWorkerTests.Claims(scratch), bundle,
            $"--cache \"{Path.Combine(scratch, "cache")}\" {flags} --output-junit \"{junit}\"", dependencyOnly, folders);
        return new Run(exit, output, junit);
    }

    private static string Test => Fixture("JobsTddRerun/test");
    private static string App => Fixture("JobsTddRerun/app");

    /// <summary>The oracle: both folders in one process, no claims.</summary>
    private static readonly Lazy<Run> Plain = new(() =>
    {
        var scratch = TestScratch.Dir("al-runner-jobs-tdd-dep-only-plain");
        var junit = Path.Combine(scratch, "run.xml");
        var (exit, stdout, stderr) = ResumeRun.Runner(
            $"--cache \"{Path.Combine(scratch, "cache")}\" --tdd --output-junit \"{junit}\" \"{Test}\" \"{App}\"");
        return new Run(exit, stdout + "\n" + stderr, junit);
    });

    /// <summary>The worker of the issue: it runs `test`, compiles `app` and does not run it.</summary>
    private static readonly Lazy<Run> DependencyOnlyWorker = new(() =>
        Worker("al-runner-jobs-tdd-dep-only-worker", Test, "--tdd", App, Test, App));

    private static List<string> Names(Run r)
        => JobsSharedBundleTddExcludedTests.Cases(r.Junit).Select(JobsSharedBundleTddExcludedTests.Name)
            .OrderBy(n => n, StringComparer.Ordinal).ToList();

    private static readonly string[] Extras =
        { "Codeunit51122.Extra1_Runs", "Codeunit51123.Extra2_Runs", "Codeunit51124.Extra3_Runs",
          "Codeunit51125.Extra4_Runs", "Codeunit51126.Extra5_Runs" };

    /// <summary>The premise: one process re-runs (a member is generated into `app`) and reports every test
    /// once. Without it the tests below would measure a fixture that never re-runs.</summary>
    [SkippableFact]
    public void Oracle_APlainRun_ReRunsAndReportsEveryTest()
    {
        TestArtifacts.SkipIfMissing();
        var plain = Plain.Value;

        Assert.Equal(1, plain.Exit);
        Assert.Contains(Recompiling, plain.Output);
        Assert.Contains("Tests: 8   passed 6   failed 2   errors 0", plain.Output);
        Assert.Subset(Names(plain).ToHashSet(), Extras.ToHashSet());
    }

    /// <summary>The defect: the worker re-runs, and every test codeunit of its bundle still reports, once,
    /// exactly as in the plain run (3 tests before the fix: the five Extra codeunits were in no row).</summary>
    [SkippableFact]
    public void AWorkerThatRunsOneBundleAndCompilesADependencyOnlyFolder_ReportsEveryTestCodeunit()
    {
        TestArtifacts.SkipIfMissing();
        var worker = DependencyOnlyWorker.Value;

        Assert.Equal(1, worker.Exit);
        Assert.Contains(Recompiling, worker.Output);
        Assert.Contains("Tests: 8   passed 6   failed 2   errors 0", worker.Output);
        var names = Names(worker);
        Assert.Equal(Names(Plain.Value), names);
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Subset(names.ToHashSet(), Extras.ToHashSet());
    }

    /// <summary>Control: a dependency-only folder whose compile generates nothing (`top` needs no member of
    /// `mid` or `base`): no re-run, and the worker reports its folder's tests as it did.</summary>
    [SkippableFact]
    public void ADependencyOnlyFolderThatGeneratesNothing_IsUnchanged_NoReRun()
    {
        TestArtifacts.SkipIfMissing();
        var top = Fixture("JobsSourceDeps/top");
        var mid = Fixture("JobsSourceDeps/mid");
        var baseFolder = Fixture("JobsSourceDeps/base");
        var run = Worker("al-runner-jobs-tdd-dep-only-nothing", top, "--tdd", $"{mid}|{baseFolder}", top, mid, baseFolder);

        Assert.Equal(0, run.Exit);
        Assert.DoesNotContain(Recompiling, run.Output);
        Assert.Contains("Tests: 3   passed 3   failed 0   errors 0", run.Output);
        Assert.Equal(new[] { "Codeunit65720.TopA_ReachesBaseThroughMid", "Codeunit65721.TopB_ReachesBaseThroughMid",
            "Codeunit65722.TopC_ReachesBaseThroughMid" }, Names(run));
    }

    /// <summary>Control: the same dependency-only layout without --tdd, which has no re-run to protect
    /// against: unchanged.</summary>
    [SkippableFact]
    public void ADependencyOnlyFolderWithoutTdd_IsUnchanged()
    {
        TestArtifacts.SkipIfMissing();
        var top = Fixture("JobsSourceDeps/top");
        var mid = Fixture("JobsSourceDeps/mid");
        var baseFolder = Fixture("JobsSourceDeps/base");
        var run = Worker("al-runner-jobs-tdd-dep-only-no-tdd", top, "", $"{mid}|{baseFolder}", top, mid, baseFolder);

        Assert.Equal(0, run.Exit);
        Assert.Contains("Tests: 3   passed 3   failed 0   errors 0", run.Output);
        Assert.Equal(3, Names(run).Count);
    }

    /// <summary>Control: a worker with one bundle and no dependency-only folder under --tdd: unchanged.</summary>
    [SkippableFact]
    public void AWorkerOfOneBundleWithNoDependencyOnlyFolder_IsUnchanged()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Fixture("JobsUnitClaim");
        var run = Worker("al-runner-jobs-tdd-dep-only-single", bundle, "--tdd", null);

        Assert.Equal(0, run.Exit);
        Assert.DoesNotContain(Recompiling, run.Output);
        Assert.Matches(new Regex(@"Tests: 10   passed 10   failed 0   errors 0"), run.Output);
        Assert.Equal(10, Names(run).Count);
    }
}
