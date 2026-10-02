// JobsSharedBundleEndToEndTests — the two shared-bundle paths of `--jobs` (#5130) that a happy-path
// run does not reach (#5215): a worker that hangs and resumes, and the `--per-suite` loop. Both
// spawn real workers; the pure half of the abort accounting is JobsSharedBundleAbortTests.
//
// What must hold however the claims fall: every test of every NON-hung codeunit runs exactly once
// across both workers and the resume, and the hung one is reported.

using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace AlRunner.Tests;

public sealed class JobsSharedBundleEndToEndTests
{
    private static string Cases(string junit) => string.Join(",",
        XDocument.Load(junit).Descendants("testcase")
            .Select(e => e.Attribute("name")!.Value).OrderBy(n => n, StringComparer.Ordinal));

    private static void WriteCodeunit(string dir, int id, string name, params string[] procedures)
    {
        var body = string.Join("\n", procedures);
        File.WriteAllText(Path.Combine(dir, $"Cu{id}.Codeunit.al"),
            $"codeunit {id} \"{name}\"\n{{\n    Subtype = Test;\n{body}\n}}\n");
    }

    private static string Test(string name, string statement)
        => $"\n    [Test]\n    procedure {name}()\n    begin\n        {statement}\n    end;\n";

    /// <summary>
    /// Claim order is largest first, so one worker claims the hanging codeunit and the other the
    /// long one (three tests that sleep 1.5 s each), which keeps it busy while the first aborts:
    /// the three small codeunits are still unclaimed at that moment, the abort abandons them and
    /// the hung worker resumes. The resumed process must keep claiming through the directory it
    /// inherits, or it would run the long codeunit a second time.
    /// </summary>
    [SkippableFact]
    public void AWorkerThatHangsInASharedBundle_Resumes_AndEveryOtherTestRunsExactlyOnce()
    {
        TestArtifacts.SkipIfMissing();
        var scratch = TestScratch.Dir("al-runner-jobs-shared-abort");
        var bundle = Path.Combine(scratch, "bundle");
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, "app.json"), """
        {
          "id": "e5f6a7b8-c9d0-4123-8456-7890abcdef45",
          "name": "Jobs Shared Bundle Abort Fixture",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": 62220, "to": 62229 } ],
          "runtime": "14.0"
        }
        """);
        // four tests: the first hangs, the other three never run (a hung codeunit is excluded whole)
        WriteCodeunit(bundle, 62221, "Shared Hang",
            Test("Hangs", "while true do;"), Test("Never1", ""), Test("Never2", ""), Test("Never3", ""));
        WriteCodeunit(bundle, 62222, "Shared Long",
            Test("Long1", "Sleep(1500);"), Test("Long2", "Sleep(1500);"), Test("Long3", "Sleep(1500);"));
        WriteCodeunit(bundle, 62223, "Shared Small A", Test("SmallA1", ""), Test("SmallA2", ""));
        WriteCodeunit(bundle, 62224, "Shared Small B", Test("SmallB1", ""), Test("SmallB2", ""));
        WriteCodeunit(bundle, 62225, "Shared Small C", Test("SmallC1", ""), Test("SmallC2", ""));
        var junit = Path.Combine(scratch, "merged.xml");

        var (exit, output) = JobsUnitClaimEndToEndTests.RunRunner(
            $"--cache \"{Path.Combine(scratch, "cache")}\" --jobs 2 --test-timeout 3 --output-junit \"{junit}\" \"{bundle}\"",
            lowSplitFloor: true);

        Assert.NotEqual(0, exit);
        Assert.Contains("is shared by 2 worker(s)", output);
        // the precondition: the hang really aborted the suite, and the abort abandoned codeunits
        // nobody had claimed, so it was worth a resume
        Assert.Contains("SUITE ABORTED", output);
        Assert.Contains("resume: a watchdog abort ended this attempt early", output);

        Assert.True(File.Exists(junit), $"--output-junit was not written.\n{output}");
        Assert.Equal(
            "Hangs,Long1,Long2,Long3,SmallA1,SmallA2,SmallB1,SmallB2,SmallC1,SmallC2",
            Cases(junit));
        // the hung codeunit is reported, as the one failing case
        var root = XDocument.Load(junit).Root!;
        Assert.Equal("1", root.Attribute("errors")!.Value);
        Assert.Equal("0", root.Attribute("failures")!.Value);
        Assert.Equal("Hangs", root.Descendants("testcase")
            .Single(e => e.Elements("error").Any()).Attribute("name")!.Value);
    }

    /// <summary>`--per-suite` compiles one module per suite and walks them in its own loop, which
    /// needs its own claim queue. Without it every worker runs every codeunit and each test is
    /// reported once per worker.</summary>
    [SkippableFact]
    public void PerSuite_OneBundle_IsSharedByTheWorkers_AndEveryTestRunsExactlyOnce()
    {
        TestArtifacts.SkipIfMissing();
        var scratch = TestScratch.Dir("al-runner-jobs-shared-persuite");
        var junit = Path.Combine(scratch, "merged.xml");

        var (exit, output) = JobsUnitClaimEndToEndTests.RunRunner(
            $"--cache \"{Path.Combine(scratch, "cache")}\" --jobs 2 --per-suite --output-junit \"{junit}\" \"{FixtureDir()}\"",
            lowSplitFloor: true);

        Assert.True(exit == 0, $"expected exit 0, got {exit}.\n{output}");
        Assert.Contains("is shared by 2 worker(s)", output);
        Assert.Contains("Tests: 10   passed 10", output);
        var shardCounts = JobsUnitClaimEndToEndTests.ShardTestCounts(output);
        Assert.Equal(2, shardCounts.Count);
        Assert.Equal(10, shardCounts.Sum());
        Assert.All(shardCounts, c => Assert.True(c > 0, $"a worker ran nothing: {string.Join("/", shardCounts)}"));
        Assert.Equal(10, Regex.Matches(File.ReadAllText(junit), "<testcase ").Count);
    }

    private static string FixtureDir() => Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        "AlRunner.Tests", "Fixtures", "JobsUnitClaim");

    /// <summary>
    /// Free memory limits sharing (#5216). `--jobs 3` on the eight-file fixture would share it by
    /// three, but 4,500 MB free at 80% headroom is 3,600, which holds two workers (about 1,400 MB
    /// each under the default model) and not three, so the bundle is shared by two, the plan says
    /// so, and every test still runs once.
    /// </summary>
    [SkippableFact]
    public void FreeMemoryThatHoldsTwoWorkers_SharesTheBundleByTwo_AndSaysWhy()
    {
        TestArtifacts.SkipIfMissing();
        var scratch = TestScratch.Dir("al-runner-jobs-shared-memory-two");

        var (exit, output) = JobsUnitClaimEndToEndTests.RunRunner(
            $"--cache \"{Path.Combine(scratch, "cache")}\" --jobs 3 \"{FixtureDir()}\"",
            lowSplitFloor: true, freeMemoryMb: "4500");

        Assert.True(exit == 0, $"expected exit 0, got {exit}.\n{output}");
        Assert.Contains("jobs: free memory holds fewer workers per bundle than --jobs asked for", output);
        Assert.Contains("by 2 worker(s), not 3", output);
        Assert.Contains("is shared by 2 worker(s)", output);
        Assert.Contains("Tests: 10   passed 10", output);
        var shardCounts = JobsUnitClaimEndToEndTests.ShardTestCounts(output);
        Assert.Equal(2, shardCounts.Count);
        Assert.Equal(10, shardCounts.Sum());
    }

    /// <summary>Free memory that holds one worker shares nothing: the bundle runs in this process,
    /// as it did before sharing, and the line says why `--jobs 2` did not fan out.</summary>
    [SkippableFact]
    public void FreeMemoryThatHoldsOneWorker_RunsTheBundleInOneProcess_AndSaysWhy()
    {
        TestArtifacts.SkipIfMissing();
        var scratch = TestScratch.Dir("al-runner-jobs-shared-memory-one");

        var (exit, output) = JobsUnitClaimEndToEndTests.RunRunner(
            $"--cache \"{Path.Combine(scratch, "cache")}\" --jobs 2 \"{FixtureDir()}\"",
            lowSplitFloor: true, freeMemoryMb: "1000");

        Assert.True(exit == 0, $"expected exit 0, got {exit}.\n{output}");
        Assert.Contains("by 1 worker(s), not 2", output);
        Assert.Contains("the bundle runs in one process", output);
        Assert.DoesNotContain("is shared by", output);
        Assert.DoesNotContain("worker process(es)", output);
        Assert.Contains("Tests: 10   passed 10", output);
    }

    /// <summary>A free-memory override that is set but unusable is said, not dropped: the one who set
    /// it meant to change what the plan sees, and the run goes on with the machine's reading.</summary>
    [SkippableFact]
    public void AnUnusableFreeMemoryOverride_IsNamedInTheOutput()
    {
        TestArtifacts.SkipIfMissing();
        var scratch = TestScratch.Dir("al-runner-jobs-shared-memory-typo");

        var (_, output) = JobsUnitClaimEndToEndTests.RunRunner(
            $"--cache \"{Path.Combine(scratch, "cache")}\" --jobs 2 \"{FixtureDir()}\"",
            lowSplitFloor: true, freeMemoryMb: "8GB");

        Assert.Contains("jobs: ignoring AL_RUNNER_JOBS_FREE_MEMORY_MB='8GB'", output);
        Assert.Contains("Tests: 10   passed 10", output);
    }
}
