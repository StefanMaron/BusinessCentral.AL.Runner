// JobsSharedBundleEmitExcludedTests — a shared `--jobs` bundle with a test codeunit that cannot bind
// (#5256). Every worker compiles the bundle and finds the same drop, and each used to report the
// dropped codeunit's [Test] procedures as SKIPPED, so the aggregate counted them once per worker.
//
// One run of the fixture serves every test here: the Lazy is the only subprocess. The claim falls
// to whichever worker is first, so the assertions hold however the codeunits are dealt out.

using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace AlRunner.Tests;

public sealed class JobsSharedBundleEmitExcludedTests
{
    // 3 healthy codeunits of 2 tests, and one dropped codeunit of 3 tests
    private const int Healthy = 6;
    private const int Dropped = 3;

    private static readonly Lazy<(int Exit, string Output, string Junit)> Run = new(() =>
    {
        var scratch = TestScratch.Dir("al-runner-jobs-shared-emit-excluded");
        var junit = Path.Combine(scratch, "merged.xml");
        var fixture = Path.Combine(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
            "AlRunner.Tests", "Fixtures", "JobsUnitClaimExcluded");
        var (exit, output) = JobsUnitClaimEndToEndTests.RunRunner(
            $"--cache \"{Path.Combine(scratch, "cache")}\" --jobs 2 --output-junit \"{junit}\" \"{fixture}\"",
            lowSplitFloor: true);
        return (exit, output, junit);
    });

    /// <summary>The aggregate counts the dropped codeunit's tests once: 3 skipped of 9, as a single
    /// process reports them, not 6 of 12.</summary>
    [SkippableFact]
    public void TheDroppedCodeunitsSkippedTests_AreCountedOnceInTheAggregate()
    {
        TestArtifacts.SkipIfMissing();
        var (_, output, _) = Run.Value;

        Assert.Contains("is shared by 2 worker(s)", output);
        Assert.Contains($"Tests: {Healthy + Dropped}   passed {Healthy}   failed 0   errors 0   skipped {Dropped}", output);
        Assert.Equal(Healthy + Dropped, JobsUnitClaimEndToEndTests.ShardTestCounts(output).Sum());
    }

    /// <summary>The report the caller asked for holds each case once, and the dropped codeunit's
    /// three are the skipped ones.</summary>
    [SkippableFact]
    public void TheMergedJUnit_HoldsEachCaseOnce()
    {
        TestArtifacts.SkipIfMissing();
        var (_, output, junit) = Run.Value;

        Assert.True(File.Exists(junit), $"--output-junit was not written.\n{output}");
        var cases = XDocument.Load(junit).Descendants("testcase").ToList();
        var names = cases.Select(e => $"{e.Attribute("classname")!.Value}.{e.Attribute("name")!.Value}").ToList();
        Assert.Equal(Healthy + Dropped, names.Count);
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Equal(new[] { "Dropped_A", "Dropped_B", "Dropped_C" },
            cases.Where(e => e.Elements("skipped").Any())
                .Select(e => e.Attribute("name")!.Value).OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>The run is still a failed one (exit 3, the bundle is partial), and a worker that left
    /// the drop to its peer is not reported as a bundle that failed to compile: the aggregate says
    /// one partial bundle and no NOT RUN line, as it does for one process.</summary>
    [SkippableFact]
    public void TheBundleIsOnePartialBundle_NotAFailedCompile_AndTheExitCodeIsUnchanged()
    {
        TestArtifacts.SkipIfMissing();
        var (exit, output, _) = Run.Value;

        Assert.Equal(3, exit);
        Assert.Contains("PARTIAL:     1 bundle(s)", output);
        Assert.DoesNotContain("NOT RUN:", output);
        Assert.DoesNotContain("— COMPILE FAIL ===", output);
        // both workers still name the drop, in their own output
        Assert.Equal(2, Regex.Matches(output, @"^<bundled>: EMIT-EXCLUDED — ", RegexOptions.Multiline).Count);
    }
}
