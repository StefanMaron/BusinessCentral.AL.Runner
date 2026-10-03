// JobsSourceDependencyEndToEndTests — `--jobs` over source folders that depend on each other (#5267).
//
// Fixtures/JobsSourceDeps: base <- mid <- {top, top2, other}, top2 also on base, plus an independent
// solo. base and mid have tests of their own. The plan under --jobs 2 deals base, other and top2 to one
// worker and top, mid and solo to the other, so each worker holds a folder that depends on one the
// other worker runs. Mid is on a worker without base: an impl whose own dependency is elsewhere,
// the shape the issue filed.
//
// The oracle is the same folders without --jobs. Without the fix the worker holding mid has no package
// for base and the run ends before one of its tests ran, with exit 2.
//
// Every folder sits under a parent of its own here, so a sibling directory scan cannot find a
// dependency: only the folders named on the command line can serve it. JobsSourceDependencySameParentTests
// runs the layout as filed, where the folders share a parent.

using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace AlRunner.Tests;

public sealed class JobsSourceDependencyEndToEndTests
{
    // base 3, mid 1, top 3, top2 2, other 2, solo 2
    internal const int Tests = 13;

    internal static readonly string[] Folders = { "base", "mid", "top", "top2", "other", "solo" };

    internal static readonly string FixtureRoot = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        "AlRunner.Tests", "Fixtures", "JobsSourceDeps");

    /// <summary>Each fixture folder copied under its own parent, `&lt;scratch&gt;/p-&lt;name&gt;/&lt;name&gt;`:
    /// the parents sort the way the folders do, so the plan is the one a shared parent gives.</summary>
    internal static List<string> CopyToSeparateParents(string scratch)
    {
        var bundles = new List<string>();
        foreach (var f in Folders)
        {
            var dest = Path.Combine(scratch, "p-" + f, f);
            Directory.CreateDirectory(dest);
            foreach (var file in Directory.GetFiles(Path.Combine(FixtureRoot, f)))
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
            bundles.Add(dest);
        }
        return bundles;
    }

    internal static string Quoted(IEnumerable<string> paths) => string.Join(" ", paths.Select(p => $"\"{p}\""));

    internal static List<string> CaseNames(string junit) => XDocument.Load(junit).Descendants("testcase")
        .Select(e => $"{e.Attribute("classname")!.Value}.{e.Attribute("name")!.Value}")
        .OrderBy(n => n, StringComparer.Ordinal).ToList();

    internal record Scenario(
        (int Exit, string Output, string Junit) Plain,
        (int Exit, string Output, string Junit) Cold,
        (int Exit, string Output, string Junit) Warm);

    private static readonly Lazy<Scenario> Run = new(() =>
    {
        var scratch = TestScratch.Dir("al-runner-jobs-source-deps");
        var bundles = Quoted(CopyToSeparateParents(scratch));
        (int, string, string) One(string name, string flags, string cache)
        {
            var junit = Path.Combine(scratch, name + ".xml");
            var (exit, output) = JobsUnitClaimEndToEndTests.RunRunner(
                $"--cache \"{Path.Combine(scratch, cache)}\" {flags} --output-junit \"{junit}\" {bundles}",
                lowSplitFloor: false);
            return (exit, output, junit);
        }
        var plain = One("plain", "", "cache-plain");
        // cold, then warm against the SAME cache root: the second run reads what the first wrote
        var cold = One("cold", "--jobs 2", "cache-jobs");
        var warm = One("warm", "--jobs 2", "cache-jobs");
        return new Scenario(plain, cold, warm);
    });

    /// <summary>The fan-out's own total, the only `Tests:` line that ends in the skipped count: each
    /// worker's line above it ends in its wall time.</summary>
    internal static string AggregateLine(string output)
        => Regex.Match(output, @"^Tests: \d+   passed \d+   failed \d+   errors \d+   skipped \d+$", RegexOptions.Multiline).Value;

    /// <summary>The oracle: the folders without --jobs. If this fails the fixture is wrong, not the fan-out.</summary>
    [SkippableFact]
    public void Oracle_ThePlainRun_PassesEveryTest()
    {
        TestArtifacts.SkipIfMissing();
        var (exit, output, junit) = Run.Value.Plain;

        Assert.True(exit == 0, output);
        Assert.Contains($"Tests: {Tests}   passed {Tests}   failed 0   errors 0", output);
        Assert.Equal(Tests, CaseNames(junit).Count);
    }

    /// <summary>The issue: source folders that depend on each other, split across workers, run and agree
    /// with the plain run on what ran, once each, and on the verdict.</summary>
    [SkippableFact]
    public void JobsRun_EqualsThePlainRun_TotalsOutcomesAndEachTestOnce()
    {
        TestArtifacts.SkipIfMissing();
        var plain = Run.Value.Plain;
        var (exit, output, junit) = Run.Value.Cold;

        Assert.True(exit == 0, output);
        Assert.DoesNotContain("A required dependency package is missing", output);
        Assert.Contains("across 2 worker process(es)", output);
        Assert.Equal($"Tests: {Tests}   passed {Tests}   failed 0   errors 0   skipped 0", AggregateLine(output));

        var names = CaseNames(junit);
        Assert.Equal(CaseNames(plain.Junit), names);
        // base and mid have tests and are also dependencies of folders on the other worker: each of
        // their tests is in the report once, not once per worker that compiled the folder
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Single(names, n => n.EndsWith(".MidValue_Is11"));
        Assert.Single(names, n => n.EndsWith(".BaseValue_Is10"));
    }

    /// <summary>The plan is still a plan: the dependent folders are not collapsed onto one worker. Both
    /// workers run tests, and independent solo is not pulled along with anything.</summary>
    [SkippableFact]
    public void IndependentFolders_StillShardAcrossWorkers_AndEachWorkerSaysWhatItOnlyCompiles()
    {
        TestArtifacts.SkipIfMissing();
        var output = Run.Value.Cold.Output;

        var perShard = JobsUnitClaimEndToEndTests.ShardTestCounts(output);
        Assert.Equal(2, perShard.Count);
        Assert.All(perShard, n => Assert.True(n > 0, output));
        Assert.Equal(Tests, perShard.Sum());

        var planLines = Regex.Matches(output, @"^jobs:   shard \d+: .*$", RegexOptions.Multiline)
            .Select(m => m.Value).ToList();
        Assert.Equal(2, planLines.Count);
        // one folder each: mid for the worker holding other and top2, base for the one holding mid
        Assert.All(planLines, l => Assert.Contains("plus 1 source dependency folder(s) it compiles and does not run", l));
        Assert.Contains(planLines, l => l.EndsWith(": mid"));
        Assert.Contains(planLines, l => l.EndsWith(": base"));
    }

    /// <summary>Two workers need base, and on an empty cache both reach it together. The package is
    /// written once and the other worker reads it: without the lock one worker found the other's
    /// half-written sidecar and the run ended with a sharing violation.</summary>
    [SkippableFact]
    public void ColdCache_TheSharedDependencyIsBuiltOnce_AndTheOtherWorkerReadsIt()
    {
        TestArtifacts.SkipIfMissing();
        var output = Run.Value.Cold.Output;

        Assert.DoesNotContain("being used by another process", output);
        Assert.Single(Regex.Matches(output, @"^\[layered\] WROTE Runner Tests Fixture - Jobs Deps Base ", RegexOptions.Multiline));
        Assert.Single(Regex.Matches(output, @"^\[layered\] cache HIT Runner Tests Fixture - Jobs Deps Base ", RegexOptions.Multiline));
    }

    /// <summary>Run twice against one cache root: the warm run reads what the cold one wrote and
    /// reports the same thing.</summary>
    [SkippableFact]
    public void WarmCache_ReportsTheSameRun()
    {
        TestArtifacts.SkipIfMissing();
        var (_, _, coldJunit) = Run.Value.Cold;
        var (exit, output, junit) = Run.Value.Warm;

        Assert.True(exit == 0, output);
        Assert.Equal(AggregateLine(Run.Value.Cold.Output), AggregateLine(output));
        Assert.Equal(CaseNames(coldJunit), CaseNames(junit));
        Assert.DoesNotMatch(@"(?m)^\[layered\] WROTE ", output);
    }
}

/// <summary>The layout the issue filed: the folders share a parent. A sibling scan can find the folders
/// no worker holds, but a worker's layered pre-pass resolves an impl's own dependencies only among
/// the folders it was handed, which is where this failed.</summary>
public sealed class JobsSourceDependencySameParentTests
{
    private static readonly Lazy<(int Exit, string Output, string Junit)> Run = new(() =>
    {
        var scratch = TestScratch.Dir("al-runner-jobs-source-deps-same-parent");
        var junit = Path.Combine(scratch, "jobs.xml");
        var bundles = JobsSourceDependencyEndToEndTests.Quoted(
            JobsSourceDependencyEndToEndTests.Folders.Select(
                f => Path.Combine(JobsSourceDependencyEndToEndTests.FixtureRoot, f)));
        var (exit, output) = JobsUnitClaimEndToEndTests.RunRunner(
            $"--cache \"{Path.Combine(scratch, "cache")}\" --jobs 2 --output-junit \"{junit}\" {bundles}",
            lowSplitFloor: false);
        return (exit, output, junit);
    });

    // other and top2 are listed, and both depend on mid and, through it, on base: two folders next to
    // them that nothing on the command line names. The workers find them by a sibling scan, each builds them,
    // and the plan hands them nothing, so this is the other place two workers write one workspace directory.
    private static readonly Lazy<(int Exit, string Output, string Junit)> RunUnlisted = new(() =>
    {
        var scratch = TestScratch.Dir("al-runner-jobs-source-deps-unlisted");
        var junit = Path.Combine(scratch, "jobs.xml");
        var bundles = JobsSourceDependencyEndToEndTests.Quoted(new[] { "other", "top2" }.Select(
            f => Path.Combine(JobsSourceDependencyEndToEndTests.FixtureRoot, f)));
        var (exit, output) = JobsUnitClaimEndToEndTests.RunRunner(
            $"--cache \"{Path.Combine(scratch, "cache")}\" --jobs 2 --output-junit \"{junit}\" {bundles}",
            lowSplitFloor: false);
        return (exit, output, junit);
    });

    /// <summary>Folders no one listed, needed by both workers: on an empty cache each source dependency
    /// is built once and the other worker reads it, as for a listed one. Without the lock the workers
    /// wrote the same package and sidecar at once.</summary>
    [SkippableFact]
    public void AnUnlistedSiblingDependencyNeededByBothWorkers_IsBuiltOnce_AndEveryTestRunsOnce()
    {
        TestArtifacts.SkipIfMissing();
        var (exit, output, junit) = RunUnlisted.Value;

        Assert.True(exit == 0, output);
        Assert.DoesNotContain("being used by another process", output);
        Assert.Equal("Tests: 4   passed 4   failed 0   errors 0   skipped 0",
            JobsSourceDependencyEndToEndTests.AggregateLine(output));
        Assert.Equal(4, JobsSourceDependencyEndToEndTests.CaseNames(junit).Count);
        foreach (var dep in new[] { "Base", "Mid" })
        {
            Assert.Single(Regex.Matches(output, $@"^\[source-dep\] WROTE Runner Tests Fixture - Jobs Deps {dep} ", RegexOptions.Multiline));
            Assert.Single(Regex.Matches(output, $@"^\[source-dep\] cache HIT Runner Tests Fixture - Jobs Deps {dep} ", RegexOptions.Multiline));
        }
    }

    [SkippableFact]
    public void JobsRun_OverFoldersThatShareAParent_PassesEveryTestOnce()
    {
        TestArtifacts.SkipIfMissing();
        var (exit, output, junit) = Run.Value;

        Assert.True(exit == 0, output);
        Assert.Equal(
            $"Tests: {JobsSourceDependencyEndToEndTests.Tests}   passed {JobsSourceDependencyEndToEndTests.Tests}   failed 0   errors 0   skipped 0",
            JobsSourceDependencyEndToEndTests.AggregateLine(output));
        var names = JobsSourceDependencyEndToEndTests.CaseNames(junit);
        Assert.Equal(JobsSourceDependencyEndToEndTests.Tests, names.Count);
        Assert.Equal(names.Count, names.Distinct().Count());
    }
}

/// <summary>The issue's smallest shape: a library and the folder depending on it, two workers. The
/// dependent's worker runs one folder and only compiles the other, so it reports and audits as a worker
/// with one folder: no `[n/m]` progress line, and no expectations entry of the folder it did not run.</summary>
public sealed class JobsSourceDependencyTwoFoldersTests
{
    private static readonly Lazy<(int Exit, string Output)> Run = new(() =>
    {
        var scratch = TestScratch.Dir("al-runner-jobs-source-deps-two");
        var fixtures = Path.GetDirectoryName(JobsSourceDependencyEndToEndTests.FixtureRoot)!;
        // The entry is scoped to the base folder and skips one of its tests. --expectations-require-match
        // fails a run whose audit finds an in-scope entry that matched no discovered test.
        return JobsUnitClaimEndToEndTests.RunRunner(
            // --failures-only: the test host's own environment can turn PASS lines and the progress line on
            $"--cache \"{Path.Combine(scratch, "cache")}\" --jobs 2 --failures-only "
            + $"--expectations \"{Path.Combine(fixtures, "JobsSourceDepsExpectations")}\" --expectations-require-match "
            + JobsSourceDependencyEndToEndTests.Quoted(new[] { "base", "mid" }.Select(
                f => Path.Combine(JobsSourceDependencyEndToEndTests.FixtureRoot, f))),
            lowSplitFloor: false);
    });

    [SkippableFact]
    public void TheDependentsWorker_CompilesTheLibraryAndRunsOnlyItsOwnFolder()
    {
        TestArtifacts.SkipIfMissing();
        var (exit, output) = Run.Value;

        Assert.True(exit == 0, output);
        // base: 3 tests, one skipped by the entry; mid: 1. The library's tests are counted once.
        Assert.Equal("Tests: 4   passed 3   failed 0   errors 0   skipped 1",
            JobsSourceDependencyEndToEndTests.AggregateLine(output));
        Assert.Contains(": 1 bundle(s), weight 1, plus 1 source dependency folder(s) it compiles and does not run: base", output);
        Assert.False(Regex.IsMatch(output, @"^\[\d+/\d+\]", RegexOptions.Multiline), output);
    }

    /// <summary>The library's expectations entry is audited by the worker that runs the library and
    /// reported as another suite's by the one that only compiles it. Counting the compiled folder as
    /// run would make that worker call the entry unmatched, and fail the whole run.</summary>
    [SkippableFact]
    public void AnExpectationsEntryOfTheCompiledFolder_IsNotAuditedByTheWorkerThatDoesNotRunIt()
    {
        TestArtifacts.SkipIfMissing();
        var (_, output) = Run.Value;

        Assert.Contains("all 1 entry in scope for this run matched a discovered test.", output);
        Assert.Contains("1 scoped to another suite, not audited here (Jobs Dep Base Tests 3.BaseValue_IsPositive)", output);
        Assert.DoesNotContain("UNMATCHED", output);
    }
}
