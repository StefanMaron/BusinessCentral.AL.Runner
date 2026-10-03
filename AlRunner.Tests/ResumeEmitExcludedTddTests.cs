// ResumeEmitExcludedTddTests — a watchdog resume (#2280) of a `--tdd` run reports the dropped codeunits'
// FAILED rows (TDD-EXCLUDED) once, #5272: the twin of ResumeEmitExcludedPlainTests' SKIPPED rows (#5268).
//
// Both attempts compile each bundle and find the same drops. The first reports a dropped codeunit's
// [Test] procedures as FAILED and carries them forward; the resumed one must not report them again.
// ResumeEmitExcluded hangs, so the run resumes; the partner and the compile-fail bundle have a dropped
// test codeunit of one test each, and the no-tests bundle a dropped helper that declares none. TddLibDropped's
// library and the last bundle come last: the last one hangs too, before a test that calls a library codeunit the
// library dropped, so that test runs in the RESUMED attempt and must still fail naming the codeunit (#5292): a
// resumed attempt is a fresh process, which fills the table of dropped codeunits again.

using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace AlRunner.Tests;

public sealed class ResumeEmitExcludedTddTests
{
    private static readonly string[] Dropped =
    {
        "Resume Excl Compile Fail.CompileFail_A", "Resume Excl Dropped.Dropped_A", "Resume Excl Dropped.Dropped_B",
        "Resume Excl Dropped.Dropped_C", "Resume Excl Partner Dropped.PartnerDropped_A",
    };
    // ResumeEmitExcluded: RanBeforeHang and four healthy tests pass, Hangs times out; the partner's one test passes;
    // The last bundle's Hangs times out too, its OtherLibraryObject_StillRuns passes and its
    // RefusedShape_FailsNamingTheDroppedObject fails.
    private const int Passed = 1 + 4 + 1 + 1;
    private const int Failed = 5 + 1;
    private const int Errors = 1 + 1;
    private const int Total = Passed + Errors + Failed;

    private static readonly Lazy<(int Exit, string Stdout, string Stderr, string Junit, string Out, string Count)> Run = new(() =>
    {
        var scratch = TestScratch.Dir("al-runner-resume-emit-excluded-tdd");
        var junit = Path.Combine(scratch, "run.xml");
        var outPath = Path.Combine(scratch, "classification.json");
        var countPath = Path.Combine(scratch, "count.json");
        var (exit, stdout, stderr) = ResumeRun.Runner(
            $"--cache \"{Path.Combine(scratch, "cache")}\" --tdd --test-timeout 3 --output-junit \"{junit}\" --output-json "
            + $"--out \"{outPath}\" --count-out \"{countPath}\" \"{ResumeRun.Fixture("ResumeEmitExcluded")}\" "
            + $"\"{ResumeRun.Fixture("ResumeEmitExcludedPartner")}\" \"{ResumeRun.Fixture("ResumeEmitExcludedNoTests")}\" "
            + $"\"{ResumeRun.Fixture("ResumeEmitExcludedCompileFail")}\" "
            + $"\"{ResumeRun.Fixture("TddLibDropped/lib")}\" \"{ResumeRun.Fixture("ResumeEmitExcludedLibDropped")}\"");
        return (exit, stdout, stderr, junit,
            File.Exists(outPath) ? File.ReadAllText(outPath) : "", File.Exists(countPath) ? File.ReadAllText(countPath) : "");
    });

    private static void AssertTheRunResumed(string stderr)
    {
        Assert.Contains("resume: a watchdog abort ended this attempt early", stderr);
        // Both attempts found the drops: it is the resumed one that must not report them again.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(stderr, @"TDD-EXCLUDED — Runner Tests Fixture - Resume Emit Excluded:").Count);
    }

    /// <summary>--output-json counts the run's twelve tests, not the dropped codeunits' once per attempt
    /// (it said 17, with ten failed).</summary>
    [SkippableFact]
    public void OutputJson_CountsEachDroppedTestOnce()
    {
        TestArtifacts.SkipIfMissing();
        var r = Run.Value;
        AssertTheRunResumed(r.Stderr);

        using var json = JsonDocument.Parse(r.Stdout);
        Assert.Equal(new[] { Total, Passed, Failed, Errors }, new[] { "total", "passed", "failed", "errors" }
            .Select(k => json.RootElement.GetProperty(k).GetInt32()));
        var names = json.RootElement.GetProperty("tests").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        // Each bundle's own row is there: the partner's and the compile-fail bundle's are not withheld because the
        // hanging bundle's were reported, and the dropped codeunit's three are not withheld by the partner's.
        foreach (var d in Dropped) Assert.Single(names, n => n == d);
        Assert.Equal(3, json.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal(3, r.Exit);
        // The closing line counts what the drops report, as it did before: the rows the resumed attempt left to the
        // carry are still reported FAILED, so it must not fall back to "no test was reported failed".
        Assert.Contains("5 [Test] procedure(s) of other dropped objects are reported FAILED above", r.Stderr);
    }

    /// <summary>
    /// #5292: the library codeunit dropped by the RESUMED attempt is still named, with its AL error, by the test
    /// that calls it. The control is the library's other codeunit, whose test passes.
    /// </summary>
    [SkippableFact]
    public void ResumedAttempt_StillNamesTheDroppedLibraryCodeunitAndItsAlError()
    {
        TestArtifacts.SkipIfMissing();
        var r = Run.Value;
        AssertTheRunResumed(r.Stderr);

        using var json = JsonDocument.Parse(r.Stdout);
        var tests = json.RootElement.GetProperty("tests").EnumerateArray().ToList();
        var refused = Assert.Single(tests, t => t.GetProperty("name").GetString()!.EndsWith(".RefusedShape_FailsNamingTheDroppedObject"));
        Assert.Equal("fail", refused.GetProperty("status").GetString());
        var failure = refused.GetProperty("message").GetString() + "\n"
            + (refused.TryGetProperty("stackTrace", out var st) ? st.GetString() : "");
        Assert.Contains("\"Lib Dropped Refused\"", failure);
        Assert.Contains("did not compile", failure);
        Assert.Contains("RefusedMissing", failure);
        Assert.DoesNotContain("provision", failure, StringComparison.OrdinalIgnoreCase);
        var other = Assert.Single(tests, t => t.GetProperty("name").GetString()!.EndsWith(".OtherLibraryObject_StillRuns"));
        Assert.Equal("pass", other.GetProperty("status").GetString());
    }

    /// <summary>--output-junit holds each case once, and the five dropped tests, with the library test, are the failed ones.</summary>
    [SkippableFact]
    public void TheJUnit_HoldsEachCaseOnce()
    {
        TestArtifacts.SkipIfMissing();
        var r = Run.Value;
        AssertTheRunResumed(r.Stderr);

        Assert.True(File.Exists(r.Junit), $"--output-junit was not written.\n{r.Stderr}");
        var cases = XDocument.Load(r.Junit).Descendants("testcase").ToList();
        var names = cases.Select(e => $"{e.Attribute("classname")!.Value}.{e.Attribute("name")!.Value}").ToList();
        Assert.Equal(Total, names.Count);
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Equal(Dropped, cases.Where(e => e.Elements("failure").Any())
            .Select(e => $"{e.Attribute("classname")!.Value}.{e.Attribute("name")!.Value}")
            .Where(n => !n.EndsWith(".RefusedShape_FailsNamingTheDroppedObject")).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Single(cases, e => e.Elements("failure").Any() && e.Attribute("name")!.Value == "RefusedShape_FailsNamingTheDroppedObject");
    }

    /// <summary>`--out` lists a failing test once, and `--count-out` counts the tests of each bundle once.</summary>
    [SkippableFact]
    public void TheClassificationAndTheCountFile_HoldEachTestOnce()
    {
        TestArtifacts.SkipIfMissing();
        var r = Run.Value;
        AssertTheRunResumed(r.Stderr);

        using var doc = JsonDocument.Parse(r.Out);
        var failing = doc.RootElement.GetProperty("all_failures").EnumerateArray()
            .Where(f => f.GetProperty("kind").GetString() is "fail" or "error")
            .Select(f => f.GetProperty("codeunit").GetString() + "." + f.GetProperty("method").GetString())
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(Dropped.Append("Codeunit50980.Hangs").Append("Codeunit72500.Hangs").Append("Codeunit72510.RefusedShape_FailsNamingTheDroppedObject")
            .OrderBy(n => n, StringComparer.Ordinal), failing);

        using var count = JsonDocument.Parse(r.Count);
        var suites = count.RootElement.GetProperty("suites");
        int Tests(string s) => suites.GetProperty(s).GetProperty("tests").GetInt32();
        Assert.Equal(new[] { 9, 2, 0, 1 }, new[]
        {
            Tests("ResumeEmitExcluded"), Tests("ResumeEmitExcludedPartner"), Tests("ResumeEmitExcludedNoTests"),
            Tests("ResumeEmitExcludedCompileFail"),
        });
    }
}
