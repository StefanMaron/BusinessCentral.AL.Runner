// ResumeEmitExcludedPlainTests — a watchdog resume (#2280) of a plain run whose bundle has an
// EMIT-EXCLUDED test codeunit (#3476), #5268.
//
// Both attempts compile the bundle and find the same drop. The first reports the dropped
// codeunit's [Test] procedures as SKIPPED and carries them forward; the resumed one must not add
// them again. One run of the fixture serves every test here: the Lazy is the only subprocess.

using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace AlRunner.Tests;

public sealed class ResumeEmitExcludedPlainTests
{
    // ResumeEmitExcluded: RanBeforeHang passes, Hangs times out, AbandonedInFirst never runs; two healthy
    // codeunits of two tests run in the resumed attempt; the dropped codeunit declares three tests.
    // The partner bundle ran in the first attempt (one passing test, one dropped test), so in the resumed
    // one it has no row of its own; the no-tests bundle has no test at all (a dropped helper, a surviving one), so it is the control that stays a failed compile.
    private const int Passed = 1 + 4 + 1;
    private const int Errors = 1;
    private const int Dropped = 3 + 1;
    private const int Total = Passed + Errors + Dropped;

    private static readonly Lazy<(int Exit, string Output, string Junit)> Run = new(() =>
    {
        var scratch = TestScratch.Dir("al-runner-resume-emit-excluded-plain");
        var junit = Path.Combine(scratch, "run.xml");
        var fixture = Path.Combine(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
            "AlRunner.Tests", "Fixtures");
        var (exit, output) = JobsUnitClaimEndToEndTests.RunRunner(
            $"--cache \"{Path.Combine(scratch, "cache")}\" --test-timeout 3 --output-junit \"{junit}\" "
            + $"\"{Path.Combine(fixture, "ResumeEmitExcluded")}\" \"{Path.Combine(fixture, "ResumeEmitExcludedPartner")}\" "
            + $"\"{Path.Combine(fixture, "ResumeEmitExcludedNoTests")}\"",
            lowSplitFloor: false);
        return (exit, output, junit);
    });

    /// <summary>The run's last counts line, as numbers: total, passed, failed, errors, skipped.</summary>
    private static int[] FinalCounts(string output)
    {
        var m = Regex.Matches(output,
            @"^Tests: (\d+)   passed (\d+)   failed (\d+)(?: \([^)]*\))?   errors (\d+)(?:   skipped (\d+))?",
            RegexOptions.Multiline).Last();
        return new[] { 1, 2, 3, 4, 5 }.Select(g => m.Groups[g].Success ? int.Parse(m.Groups[g].Value) : 0).ToArray();
    }

    private static void AssertTheRunResumed(string output)
    {
        Assert.Contains("Test exceeded 3s timeout.", output);
        Assert.Contains("resume: a watchdog abort ended this attempt early", output);
    }

    /// <summary>The summary says 11 tests, and its categories add up to the 9. Before the fix the
    /// dropped codeunit was counted by both attempts: 15 tests, categories adding up to 11.</summary>
    [SkippableFact]
    public void TheDroppedCodeunitsSkippedTests_AreCountedOnceInTheFinalSummary()
    {
        TestArtifacts.SkipIfMissing();
        var (_, output, _) = Run.Value;
        AssertTheRunResumed(output);

        var c = FinalCounts(output);
        Assert.Equal(new[] { Total, Passed, 0, Errors, Dropped }, c);
        Assert.Equal(c[0], c[1] + c[2] + c[3] + c[4]);
    }

    /// <summary>--output-junit holds each case once, and the dropped codeunit's three are the
    /// skipped ones.</summary>
    [SkippableFact]
    public void TheJUnit_HoldsEachCaseOnce()
    {
        TestArtifacts.SkipIfMissing();
        var (_, output, junit) = Run.Value;
        AssertTheRunResumed(output);

        Assert.True(File.Exists(junit), $"--output-junit was not written.\n{output}");
        var cases = XDocument.Load(junit).Descendants("testcase").ToList();
        var names = cases.Select(e => $"{e.Attribute("classname")!.Value}.{e.Attribute("name")!.Value}").ToList();
        Assert.Equal(Total, names.Count);
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Equal(new[] { "Dropped_A", "Dropped_B", "Dropped_C", "PartnerDropped_A" },
            cases.Where(e => e.Elements("skipped").Any())
                .Select(e => e.Attribute("name")!.Value).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(Dropped.ToString(), XDocument.Load(junit).Root!.Attribute("skipped")!.Value);
    }

    /// <summary>Counting the drop once must not turn the run clean or reclassify a bundle: the exit code stays
    /// 3, and the final attempt's own table says what the run is. The partner ran and has no row of its own in
    /// the resumed attempt, which is still a partial bundle (it was a failed compile, and the footer said its
    /// tests were missing from the counts). The no-tests bundle has nothing of its own and nothing carried, and stays the one failed compile.</summary>
    [SkippableFact]
    public void TheExitCodeIsUnchanged_AndBundlesThatRanStayPartial_WhileTheOneThatDidNotStaysFailed()
    {
        TestArtifacts.SkipIfMissing();
        var (exit, output, _) = Run.Value;
        AssertTheRunResumed(output);

        Assert.Equal(3, exit);
        var final = output.Substring(Regex.Matches(output, @"^Tests: \d+ ", RegexOptions.Multiline).Last().Index);
        Assert.Matches(@"(?m)^\s+ran:\s+2\b", final);
        Assert.Matches(@"(?m)^\s+compile-fail:\s*1\b", final);
        Assert.Matches(@"(?m)^\s+partial:\s+2\b", final);
        Assert.Contains("Compile failures: 1 app(s) did not compile", final);
        Assert.DoesNotMatch(@"=== ResumeEmitExcludedPartner — COMPILE FAIL ===", output);
        Assert.Matches(@"=== ResumeEmitExcludedNoTests — COMPILE FAIL ===", output);
    }
}
