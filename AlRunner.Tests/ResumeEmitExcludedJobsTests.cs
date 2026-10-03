// ResumeEmitExcludedJobsTests — a `--jobs` worker that resumes after a watchdog abort (#2280) in a
// bundle with an EMIT-EXCLUDED test codeunit (#3476), #5269 (and #5268 as the parent sees it).
//
// Each attempt prints its own `=== <bundle> — SUITE ERRORS (n) ===` header into the worker's output,
// and the aggregate's `PARTIAL:` and `NOT RUN:` lines count bundles, not headers. Three bundles: the
// sibling is the heaviest (file count), so it takes one worker alone, and the hanging bundle shares the
// other with a bundle that cannot compile. Nothing is shared between workers, so the hanging bundle's
// worker resumes however they are scheduled. One run serves every test here.

using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace AlRunner.Tests;

public sealed class ResumeEmitExcludedJobsTests
{
    private const int Total = 9 + 5;   // the hanging bundle's 9 (see ResumeEmitExcludedPlainTests) + the sibling's 5

    private static readonly Lazy<(int Exit, string Output, string Junit)> Run = new(() =>
    {
        var scratch = TestScratch.Dir("al-runner-resume-emit-excluded-jobs");
        var junit = Path.Combine(scratch, "merged.xml");
        var fixtures = Path.Combine(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
            "AlRunner.Tests", "Fixtures");
        var (exit, output) = JobsUnitClaimEndToEndTests.RunRunner(
            $"--cache \"{Path.Combine(scratch, "cache")}\" --jobs 2 --test-timeout 3 --output-junit \"{junit}\" "
            + $"\"{Path.Combine(fixtures, "ResumeEmitExcluded")}\" \"{Path.Combine(fixtures, "ResumeEmitExcludedSibling")}\" "
            + $"\"{Path.Combine(fixtures, "ResumeEmitExcludedCompileFail")}\"",
            lowSplitFloor: false);
        return (exit, output, junit);
    });

    private static void AssertTheWorkerResumed(string output)
    {
        Assert.Contains("jobs: 3 bundle(s) across 2 worker process(es)", output);
        // The compile-failed bundle is in the resuming shard: it printed its header in both attempts.
        Assert.Equal(2, Regex.Matches(output, @"^=== ResumeEmitExcludedCompileFail — COMPILE FAIL ===", RegexOptions.Multiline).Count);
        Assert.Contains("Test exceeded 3s timeout.", output);
        Assert.Contains("resume: a watchdog abort ended this attempt early", output);
    }

    /// <summary>One bundle lost a suite, however many attempts its worker made: one PARTIAL bundle, and
    /// nothing is NOT RUN. Before the fix the aggregate said 2, one per attempt.</summary>
    [SkippableFact]
    public void ABundleThatLostSuitesInEveryAttempt_IsOnePartialBundle()
    {
        TestArtifacts.SkipIfMissing();
        var (exit, output, _) = Run.Value;
        AssertTheWorkerResumed(output);

        Assert.Equal(3, exit);
        Assert.Contains("PARTIAL:     1 bundle(s)", output);
    }

    /// <summary>The same for the bundle that did not run: one COMPILE FAIL bundle, printed by both attempts of
    /// the worker that resumed, is one NOT RUN bundle (it was two).</summary>
    [SkippableFact]
    public void ABundleThatCompileFailsInEveryAttempt_IsOneNotRunBundle()
    {
        TestArtifacts.SkipIfMissing();
        var (_, output, _) = Run.Value;
        AssertTheWorkerResumed(output);

        Assert.Contains("NOT RUN:     1 bundle(s)", output);
    }

    /// <summary>The aggregate counts each test once: the resumed worker's final attempt does not add the
    /// dropped codeunit's SKIPPED tests the first attempt already reported.</summary>
    [SkippableFact]
    public void TheAggregateAndTheMergedJUnit_CountEachTestOnce()
    {
        TestArtifacts.SkipIfMissing();
        var (_, output, junit) = Run.Value;
        AssertTheWorkerResumed(output);

        Assert.Contains($"Tests: {Total}   passed 10   failed 0   errors 1   skipped 3", output);
        Assert.True(File.Exists(junit), $"--output-junit was not written.\n{output}");
        var names = XDocument.Load(junit).Descendants("testcase")
            .Select(e => $"{e.Attribute("classname")!.Value}.{e.Attribute("name")!.Value}").ToList();
        Assert.Equal(Total, names.Count);
        Assert.Equal(names.Count, names.Distinct().Count());
    }
}
