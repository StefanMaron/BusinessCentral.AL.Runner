// JobsTddDependencyOnlyTests — a `--tdd --jobs` worker that RUNS one bundle and only COMPILES a
// dependency-only folder (#5318). --tdd generates a member into the dependency-only folder and compiles the
// cycle again (#5037); a worker that ran its bundle in the first pass had claimed its test codeunits
// (UnitClaimQueue), so the second pass skipped them and nobody reported them.
//
// One process plays the worker, with the environment a `--jobs` parent hands it. The oracle is the same two
// folders in one process. Fixtures/JobsTddRerun: `app` (a codeunit with no members) and `test` (a test calling a
// member `app` lacks, five test codeunits of one test each, and a dropped test codeunit of two tests).
// The end-to-end `--jobs 2` run of the same fixture is in JobsSharedBundleTddExcludedWorkerTests.
//
// #5326: the routes that do NOT defer (AL_RUNNER_SEQUENTIAL_BUNDLES=1 with several run bundles, and --per-suite)
// run their bundle in the pass --tdd's re-run discards. The re-run gives that pass's test codeunit claims back
// (UnitClaimLedger), so the codeunits are claimed and reported again, once.

using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class JobsTddDependencyOnlyTests
{
    private static string Fixture(string name) => ResumeRun.Fixture(name);

    private const string Recompiling = "recompiling it and compiling the bundles that depend on it again";

    private sealed record Run(int Exit, string Output, string Junit);

    /// <summary>One compile cache for the runs of this class: the same folders compile in several of them, and a cache
    /// is correct under concurrent writers (CacheCompileLock). The runs are the cost of this class.</summary>
    private static readonly string SharedCache = Path.Combine(TestScratch.Dir("al-runner-jobs-tdd-dep-only-cache"), "cache");

    private static Run Worker(string scratchName, string bundle, string flags, string? dependencyOnly, params string[] folders)
        => Worker(scratchName, bundle, flags, dependencyOnly, sequentialBundles: false, folders);

    private static Run Worker(string scratchName, string bundle, string flags, string? dependencyOnly,
        bool sequentialBundles, params string[] folders)
        => WorkerIn(TestScratch.Dir(scratchName), "run", bundle, flags, dependencyOnly, sequentialBundles, folders);

    /// <summary>A worker whose claim directory is the scratch's own, so a second call over the same scratch is a peer
    /// (or this worker's later process) over the same claims; `name` keeps the two caches and JUnit files apart.</summary>
    private static Run WorkerIn(string scratch, string name, string bundle, string flags, string? dependencyOnly,
        bool sequentialBundles, params string[] folders)
    {
        var junit = Path.Combine(scratch, name + ".xml");
        var (exit, output) = JobsSharedBundleTddExcludedWorkerTests.Worker(
            JobsSharedBundleTddExcludedWorkerTests.Claims(scratch), bundle,
            $"--cache \"{SharedCache}\" {flags} --output-junit \"{junit}\"",
            dependencyOnly, sequentialBundles, folders);
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

    // ---- #5326: the runs that do not defer ----

    private static string Solo => Fixture("JobsSourceDeps/solo");
    private const string GaveBack = "gave back";

    private static Run PlainRun(string scratchName, string flags, params string[] folders)
    {
        var scratch = TestScratch.Dir(scratchName);
        var junit = Path.Combine(scratch, "run.xml");
        var (exit, stdout, stderr) = ResumeRun.Runner(
            $"--cache \"{SharedCache}\" {flags} --output-junit \"{junit}\" "
            + string.Join(" ", folders.Select(f => $"\"{f}\"")));
        return new Run(exit, stdout + "\n" + stderr, junit);
    }

    /// <summary>The oracle of the knob route: the three folders in one process, no claims.</summary>
    private static readonly Lazy<Run> PlainThree = new(() =>
        PlainRun("al-runner-jobs-tdd-seq-plain", "--tdd", Test, Solo, App));

    /// <summary>The knob route: AL_RUNNER_SEQUENTIAL_BUNDLES=1 keeps one pass per bundle, so the worker runs `test`
    /// (shared, claimed) and `solo` (two tests, not shared) in the pass --tdd's re-run discards.</summary>
    private static readonly Lazy<Run> SequentialWorker = new(() =>
        Worker("al-runner-jobs-tdd-seq-worker", Test, "--tdd", App, sequentialBundles: true, Test, Solo, App));

    /// <summary>The oracle of the per-suite route (one pass per bundle by design, so it never defers).</summary>
    private static readonly Lazy<Run> PlainPerSuite = new(() =>
        PlainRun("al-runner-jobs-tdd-persuite-plain", "--tdd --per-suite", App, Test));

    private static readonly Lazy<Run> PerSuiteWorker = new(() =>
        Worker("al-runner-jobs-tdd-persuite-worker", Test, "--tdd --per-suite", App, Test, App));

    /// <summary>A peer process (its claims are in the directory) that selects Extra1 only, then the worker over the
    /// same claims.</summary>
    private static readonly Lazy<(Run Peer, Run Worker)> PeerThenSequentialWorker = new(() =>
    {
        var scratch = TestScratch.Dir("al-runner-jobs-tdd-seq-peer");
        var peer = WorkerIn(scratch, "peer", Test, "--tdd --test Extra1_Runs", App, sequentialBundles: true, Test, Solo, App);
        var worker = WorkerIn(scratch, "worker", Test, "--tdd", App, sequentialBundles: true, Test, Solo, App);
        return (peer, worker);
    });

    [SkippableFact]
    public void Oracle_ThePlainSequentialRunsReRunAndReportEveryTest()
    {
        TestArtifacts.SkipIfMissing();
        var three = PlainThree.Value;
        var perSuite = PlainPerSuite.Value;

        Assert.Equal(1, three.Exit);
        Assert.Contains(Recompiling, three.Output);
        Assert.Contains("Tests: 10   passed 8   failed 2   errors 0", three.Output);
        Assert.Subset(Names(three).ToHashSet(), Extras.ToHashSet());
        Assert.Equal(1, perSuite.Exit);
        Assert.Contains(Recompiling, perSuite.Output);
        Assert.Contains("Tests: 8   passed 6   failed 2   errors 0", perSuite.Output);
        Assert.Subset(Names(perSuite).ToHashSet(), Extras.ToHashSet());
    }

    /// <summary>The knob route: the worker re-runs, gives its claims back and reports every test once, as the plain
    /// run does (5 tests fewer before the fix: the five Extra codeunits were in no row).</summary>
    [SkippableFact]
    public void AWorkerThatKeepsOnePassPerBundle_ReportsEveryTestCodeunitOnce()
    {
        TestArtifacts.SkipIfMissing();
        var worker = SequentialWorker.Value;

        Assert.Equal(1, worker.Exit);
        Assert.Contains(Recompiling, worker.Output);
        Assert.Contains(GaveBack, worker.Output);
        Assert.Contains("Tests: 10   passed 8   failed 2   errors 0", worker.Output);
        var names = Names(worker);
        Assert.Equal(Names(PlainThree.Value), names);
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Subset(names.ToHashSet(), Extras.ToHashSet());
    }

    /// <summary>The per-suite route: one pass per bundle by design, so it never defers; the same release serves it.</summary>
    [SkippableFact]
    public void APerSuiteWorker_ReportsEveryTestCodeunitOnce()
    {
        TestArtifacts.SkipIfMissing();
        var worker = PerSuiteWorker.Value;

        Assert.Equal(1, worker.Exit);
        Assert.Contains(GaveBack, worker.Output);
        Assert.Contains("Tests: 8   passed 6   failed 2   errors 0", worker.Output);
        var names = Names(worker);
        Assert.Equal(Names(PlainPerSuite.Value), names);
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Subset(names.ToHashSet(), Extras.ToHashSet());
    }

    /// <summary>Control: a claim ANOTHER process holds stays taken through the re-run. The peer ran Extra1 only
    /// (and the dropped objects), so the worker skips Extra1 in both passes and reports the rest; together they
    /// are the plain run's tests, each once. A release that gave back every claim in the directory would run Extra1
    /// on both.</summary>
    [SkippableFact]
    public void AClaimAPeerHolds_StaysSkippedThroughTheReRun_AndEveryTestStillReportsOnce()
    {
        TestArtifacts.SkipIfMissing();
        var (peer, worker) = PeerThenSequentialWorker.Value;

        Assert.Contains(Recompiling, worker.Output);
        Assert.Contains(GaveBack, worker.Output);
        var peerNames = Names(peer);
        var workerNames = Names(worker);
        Assert.Contains("Codeunit51122.Extra1_Runs", peerNames);
        Assert.DoesNotContain("Codeunit51122.Extra1_Runs", workerNames);
        Assert.Empty(peerNames.Intersect(workerNames));
        Assert.Equal(Names(PlainThree.Value), peerNames.Concat(workerNames).OrderBy(n => n, StringComparer.Ordinal).ToList());
    }

    /// <summary>Control: the knob with nothing to re-run (`top` needs no member of `mid` or `base`) releases nothing
    /// and reports as it did.</summary>
    [SkippableFact]
    public void AWorkerWithTheKnobAndNoReRun_IsUnchanged()
    {
        TestArtifacts.SkipIfMissing();
        var top = Fixture("JobsSourceDeps/top");
        var mid = Fixture("JobsSourceDeps/mid");
        var baseFolder = Fixture("JobsSourceDeps/base");
        var run = Worker("al-runner-jobs-tdd-seq-nothing", top, "--tdd", $"{mid}|{baseFolder}", sequentialBundles: true, top, mid, baseFolder);

        Assert.Equal(0, run.Exit);
        Assert.DoesNotContain(Recompiling, run.Output);
        Assert.DoesNotContain(GaveBack, run.Output);
        Assert.Contains("Tests: 3   passed 3   failed 0   errors 0", run.Output);
        Assert.Equal(3, Names(run).Count);
    }
}
