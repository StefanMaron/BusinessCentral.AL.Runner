// ResumeStructuredOutputsTests — a watchdog resume (#2280) reports a bundle once in the structured
// outputs, #5273 (and in --tdd's TDD-EXCLUDED rows, #5272: ResumeEmitExcludedTddTests).
//
// Every attempt compiles the bundle again and reports it, and the final attempt folds the earlier
// attempts' full results in beside its own. --output-json, --out and --count-out each treat every
// folded bucket as a bundle, so a bundle read twice. One run of three bundles serves every test here.
// ResumeEmitExcluded hangs (the run resumes) and is the bundle that reports different things in the
// two attempts: its abort only in the first, its EMIT-EXCLUDED drop in both. The partner reports the
// same drop in both and only ran in the first. The no-tests bundle cannot compile in either attempt.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>Runs the built runner once and keeps stdout apart from stderr: --output-json is the whole of
/// stdout, which the merged capture of <see cref="JobsUnitClaimEndToEndTests.RunRunner"/> cannot give back.</summary>
internal static class ResumeRun
{
    internal static readonly string Fixtures = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        "AlRunner.Tests", "Fixtures");

    internal static (int Exit, string Stdout, string Stderr) Runner(string extraArgs)
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var args = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(repoRoot, "AlRunner")));
        args.Append(TestBuildConfig.BcVersionArg).Append(' ').Append(extraArgs);
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = repoRoot,
        };
        var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(600_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        return (p.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    internal static string Fixture(string name) => Path.Combine(Fixtures, name);

    /// <summary>The entries of one of --output-json's per-bundle arrays: bundle directory name, errors.</summary>
    internal static List<(string Bundle, List<string> Errors)> BundleErrors(JsonDocument json, string property)
        => json.RootElement.TryGetProperty(property, out var arr)
            ? arr.EnumerateArray()
                .Select(e => (Path.GetFileName(e.GetProperty("file").GetString()!),
                    e.GetProperty("errors").EnumerateArray().Select(x => x.GetString()!).ToList()))
                .ToList()
            : new();
}

public sealed class ResumeStructuredOutputsTests
{
    private static readonly Lazy<(int Exit, string Stdout, string Stderr, string Out, string Count)> Run = new(() =>
    {
        var scratch = TestScratch.Dir("al-runner-resume-structured-outputs");
        var outPath = Path.Combine(scratch, "classification.json");
        var countPath = Path.Combine(scratch, "count.json");
        var (exit, stdout, stderr) = ResumeRun.Runner(
            $"--cache \"{Path.Combine(scratch, "cache")}\" --test-timeout 3 --output-json --out \"{outPath}\" "
            + $"--count-out \"{countPath}\" \"{ResumeRun.Fixture("ResumeEmitExcluded")}\" "
            + $"\"{ResumeRun.Fixture("ResumeEmitExcludedPartner")}\" \"{ResumeRun.Fixture("ResumeEmitExcludedNoTests")}\"");
        return (exit, stdout, stderr,
            File.Exists(outPath) ? File.ReadAllText(outPath) : "", File.Exists(countPath) ? File.ReadAllText(countPath) : "");
    });

    private static void AssertTheRunResumed((int Exit, string Stdout, string Stderr, string Out, string Count) r)
    {
        Assert.Contains("resume: a watchdog abort ended this attempt early", r.Stderr);
        Assert.False(string.IsNullOrEmpty(r.Stdout), $"--output-json printed nothing.\n{r.Stderr}");
    }

    /// <summary>One entry per bundle in `suiteErrors` and `compilationErrors`. The hanging bundle lists the
    /// abort only its first attempt reported next to the drop both reported, once each; the partner and the
    /// no-tests bundle list the drop once. Before the fix `suiteErrors` had four entries and
    /// `compilationErrors` two.</summary>
    [SkippableFact]
    public void OutputJson_ListsEachBundleOnce_AndKeepsWhatOnlyOneAttemptReported()
    {
        TestArtifacts.SkipIfMissing();
        var r = Run.Value;
        AssertTheRunResumed(r);

        using var json = JsonDocument.Parse(r.Stdout);
        var suite = ResumeRun.BundleErrors(json, "suiteErrors");
        Assert.Equal(new[] { "ResumeEmitExcluded", "ResumeEmitExcludedPartner" }, suite.Select(s => s.Bundle).OrderBy(b => b, StringComparer.Ordinal));
        var hanging = suite.Single(s => s.Bundle == "ResumeEmitExcluded").Errors;
        Assert.Equal(2, hanging.Count);
        Assert.Single(hanging, e => e.Contains("EMIT-EXCLUDED for"));
        Assert.Single(hanging, e => e.Contains("TEST-TIMEOUT-ABORT"));
        Assert.Single(suite.Single(s => s.Bundle == "ResumeEmitExcludedPartner").Errors);

        var compile = ResumeRun.BundleErrors(json, "compilationErrors");
        Assert.Equal(new[] { "ResumeEmitExcludedNoTests" }, compile.Select(c => c.Bundle));
        Assert.Single(compile[0].Errors);
        Assert.Equal(3, r.Exit);
    }

    /// <summary>`--out` is a triage worklist: a suite record per bundle that lost a suite, a compile record
    /// per bundle that did not compile, one record per failing test. The run's total_failures is 4 (it was 7).</summary>
    [SkippableFact]
    public void Out_HoldsOneRecordPerBundleAndPerFailingTest()
    {
        TestArtifacts.SkipIfMissing();
        var r = Run.Value;
        AssertTheRunResumed(r);

        using var doc = JsonDocument.Parse(r.Out);
        Assert.Equal(4, doc.RootElement.GetProperty("total_failures").GetInt32());
        var records = doc.RootElement.GetProperty("all_failures").EnumerateArray()
            .Select(f => (Kind: f.GetProperty("kind").GetString()!, Bundle: Path.GetFileName(f.GetProperty("bucket").GetString()!),
                Method: f.TryGetProperty("method", out var m) ? m.GetString() : null))
            .OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Bundle, StringComparer.Ordinal).ToList();
        Assert.Equal(new (string, string, string?)[]
        {
            ("compile", "ResumeEmitExcludedNoTests", null),
            ("error", "ResumeEmitExcluded", "Hangs"),
            ("suite", "ResumeEmitExcluded", null),
            ("suite", "ResumeEmitExcludedPartner", null),
        }, records);
    }

    /// <summary>`--count-out` is what `--count-baseline` judges: a suite's app groups are the groups it has, not
    /// the groups each attempt entered. One group each (it read 2 for both bundles that ran), and the no-tests
    /// bundle never ran one. The test counts are unchanged by it: 9 and 2.</summary>
    [SkippableFact]
    public void CountOut_CountsEachAppGroupOnce_AndKeepsTheTestCounts()
    {
        TestArtifacts.SkipIfMissing();
        var r = Run.Value;
        AssertTheRunResumed(r);

        using var doc = JsonDocument.Parse(r.Count);
        var suites = doc.RootElement.GetProperty("suites");
        (int Tests, int Groups) Of(string s) =>
            (suites.GetProperty(s).GetProperty("tests").GetInt32(), suites.GetProperty(s).GetProperty("appGroups").GetInt32());
        Assert.Equal((9, 1), Of("ResumeEmitExcluded"));
        Assert.Equal((2, 1), Of("ResumeEmitExcludedPartner"));
        Assert.Equal((0, 0), Of("ResumeEmitExcludedNoTests"));
    }
}
