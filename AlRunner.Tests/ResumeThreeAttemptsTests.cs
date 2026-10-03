// ResumeThreeAttemptsTests — a run that resumes TWICE reports a bundle once in the structured outputs
// (#5273) and its dropped codeunit's SKIPPED rows once (#5268), whichever attempt reported what.
//
// ResumeEmitExcludedTwoHangs hangs in two codeunits, one per attempt, and drops a third: the bundle is
// reported by three attempts. The drop is the same in all three, the first abort is only in the first
// attempt and the second only in the second, so the merged bundle must keep both aborts and the drop once.

using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class ResumeThreeAttemptsTests
{
    private static readonly Lazy<(int Exit, string Stdout, string Stderr, string Out, string Count)> Run = new(() =>
    {
        var scratch = TestScratch.Dir("al-runner-resume-three-attempts");
        var outPath = Path.Combine(scratch, "classification.json");
        var countPath = Path.Combine(scratch, "count.json");
        var (exit, stdout, stderr) = ResumeRun.Runner(
            $"--cache \"{Path.Combine(scratch, "cache")}\" --test-timeout 3 --output-json --out \"{outPath}\" "
            + $"--count-out \"{countPath}\" \"{ResumeRun.Fixture("ResumeEmitExcludedTwoHangs")}\"");
        return (exit, stdout, stderr,
            File.Exists(outPath) ? File.ReadAllText(outPath) : "", File.Exists(countPath) ? File.ReadAllText(countPath) : "");
    });

    private static void AssertTheRunResumedTwice((int Exit, string Stdout, string Stderr, string Out, string Count) r)
    {
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(r.Stderr, "resume: a watchdog abort ended this attempt early").Count);
        Assert.False(string.IsNullOrEmpty(r.Stdout), $"--output-json printed nothing.\n{r.Stderr}");
    }

    /// <summary>One bundle, three attempts: `suiteErrors` has ONE entry holding the drop once, the abort only the
    /// first attempt saw and the abort only the second saw. Before the fix it had three entries.</summary>
    [SkippableFact]
    public void OutputJson_HoldsOneEntry_WithEveryDistinctError()
    {
        TestArtifacts.SkipIfMissing();
        var r = Run.Value;
        AssertTheRunResumedTwice(r);

        using var json = JsonDocument.Parse(r.Stdout);
        var entry = Assert.Single(ResumeRun.BundleErrors(json, "suiteErrors"));
        Assert.Equal("ResumeEmitExcludedTwoHangs", entry.Bundle);
        Assert.Equal(3, entry.Errors.Count);
        Assert.Single(entry.Errors, e => e.Contains("EMIT-EXCLUDED for"));
        Assert.Single(entry.Errors, e => e.Contains("TEST-TIMEOUT-ABORT") && e.Contains("Resume Two Hangs First"));
        Assert.Single(entry.Errors, e => e.Contains("TEST-TIMEOUT-ABORT") && e.Contains("Resume Two Hangs Second"));
        Assert.Equal(3, r.Exit);
        Assert.Equal(6, json.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(2, json.RootElement.GetProperty("skipped").GetInt32());
    }

    /// <summary>`--out`: one suite record and one record per hung test (it was three suite records).</summary>
    [SkippableFact]
    public void Out_HoldsOneSuiteRecord_AndOneRecordPerHungTest()
    {
        TestArtifacts.SkipIfMissing();
        var r = Run.Value;
        AssertTheRunResumedTwice(r);

        using var doc = JsonDocument.Parse(r.Out);
        Assert.Equal(3, doc.RootElement.GetProperty("total_failures").GetInt32());
        var records = doc.RootElement.GetProperty("all_failures").EnumerateArray()
            .Select(f => f.GetProperty("kind").GetString() + ":" + (f.TryGetProperty("method", out var m) ? m.GetString() : ""))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "error:HangsFirst", "error:HangsSecond", "suite:" }, records);
    }

    /// <summary>`--count-out`: one app group, and the six tests of the run counted once.</summary>
    [SkippableFact]
    public void CountOut_CountsOneAppGroup()
    {
        TestArtifacts.SkipIfMissing();
        var r = Run.Value;
        AssertTheRunResumedTwice(r);

        using var doc = JsonDocument.Parse(r.Count);
        var suite = doc.RootElement.GetProperty("suites").GetProperty("ResumeEmitExcludedTwoHangs");
        Assert.Equal(6, suite.GetProperty("tests").GetInt32());
        Assert.Equal(1, suite.GetProperty("appGroups").GetInt32());
    }
}
