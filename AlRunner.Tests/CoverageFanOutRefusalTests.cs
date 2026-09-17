// CoverageFanOutRefusalTests — issue #3966.
//
// `--coverage` with a `--jobs` fan-out wrote every shard to ONE Cobertura path. The reported
// failure: two apps, two workers, both tests pass and the parent exits 0, and the report holds
// only the worker that finished last. Without the reporter's deliberate delay the writes overlap
// instead and the run exits 2 on a sharing violation — so the same defect is a hard error or
// silent data loss depending on timing, and the silent branch is the one that ships a number.
//
// `--count-out` already has exactly this shape and is REFUSED at the fan-out (Program.cs), with
// the reasoning written out there: each worker would write its own shard to the single path the
// caller named, and the last to finish would silently become the run's result. #3966's acceptance
// criteria allow either a merge or an explicit rejection; this takes the rejection, matching the
// established answer for the identical problem.
//
// Refused where the fan-out HAPPENS, not at parse time: `--jobs 4` with one bundle does not fan
// out, and coverage is correct in that run. That placement is load-bearing and is what the
// source-shape test below pins.

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class CoverageFanOutRefusalTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    /// <summary>
    /// The one test that can see whether the refusal REFUSES. The three source-shape tests below
    /// read Program.cs as text, so all three stay green when `return 2;` is deleted from the
    /// guard — measured in review: the mutated binary printed the refusal, fanned out anyway,
    /// and wrote a cobertura.xml holding one shard, which is #3966 reproduced underneath a
    /// warning claiming the run was refused.
    ///
    /// Same shape as CountOutTests.CountOutUnderJobs_IsRefusedLoudly_..., the precedent this
    /// refusal copies, and for the same reason: the load-bearing assertion is that NO coverage
    /// document is left behind. Exit 2 alone would not catch a refusal that still ran.
    /// </summary>
    [SkippableFact]
    public void CoverageUnderJobs_IsRefusedLoudly_AndLeavesNoCoberturaBehind()
    {
        TestArtifacts.SkipIfMissing();

        var scratch = TestScratch.Dir("al-runner-coverage-jobs");
        var coveragePath = Path.Combine(scratch, "cobertura.xml");
        var fixtures = Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures");

        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        // Two bundles, because one bundle never fans out however large --jobs is.
        args.Append($" \"{Path.Combine(fixtures, "RecordTriggerXRec")}\"");
        args.Append($" \"{Path.Combine(fixtures, "CoverageBranch")}\"");
        args.Append(" --jobs 2");
        args.Append(" --coverage");
        args.Append($" --coverage-out \"{coveragePath}\"");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(600_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        string output;
        lock (sb) output = sb.ToString();

        Assert.True(p.ExitCode == 2,
            $"expected exit 2 (bad invocation), got {p.ExitCode}.\n{output}");
        Assert.Contains("--coverage", output);
        Assert.Contains("--jobs", output);
        // The message must say what would otherwise happen, not merely that it is refused.
        Assert.Contains("shard", output);

        // THE assertion. A refusal that printed its message and then fanned out anyway leaves a
        // one-shard document here, which is the defect wearing the fix's clothes.
        Assert.False(File.Exists(coveragePath),
            "a refused invocation must leave no coverage document behind — a one-shard one would "
            + $"be #3966 reproduced under a message claiming the run was refused.\n{output}");
    }

    private static string ProgramSource()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir != null; i++)
        {
            var p = Path.Combine(dir, "AlRunner", "Program.cs");
            if (File.Exists(p)) return File.ReadAllText(p);
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("could not locate AlRunner/Program.cs from " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// The refusal exists and names the flag, so a caller who hits it knows which flag to drop.
    /// Asserted on the message text because that message IS the deliverable here — the defect is
    /// that the run said nothing at all.
    /// </summary>
    [Fact]
    public void TheFanOut_RefusesCoverage_AndSaysWhy()
    {
        var src = ProgramSource();

        Assert.Contains("--coverage cannot be combined with a --jobs fan-out", src);

        // The remedy must be in the message. A refusal that does not say what to do instead
        // turns a silent wrong answer into a loud dead end.
        var idx = src.IndexOf("--coverage cannot be combined with a --jobs fan-out", StringComparison.Ordinal);
        var message = src.Substring(idx, Math.Min(600, src.Length - idx));
        Assert.Contains("without --jobs", message);
    }

    /// <summary>
    /// The refusal sits in the fan-out branch, beside the one --count-out already has — NOT at
    /// parse time, because `--jobs N` with a single bundle never fans out and coverage is correct
    /// there. Pinned by proximity to the sibling refusal rather than by line number, which drifts.
    /// </summary>
    [Fact]
    public void TheRefusal_SitsBesideTheCountOutOne_InTheFanOutBranch()
    {
        var src = ProgramSource();

        var countOut = src.IndexOf("--count-out cannot be combined with a --jobs fan-out", StringComparison.Ordinal);
        var coverage = src.IndexOf("--coverage cannot be combined with a --jobs fan-out", StringComparison.Ordinal);

        Assert.True(countOut > 0, "the --count-out refusal must still exist; it is the precedent this copies");
        Assert.True(coverage > 0, "the --coverage refusal must exist");

        // Same guard block: both are inside the function that runs only when a fan-out is about
        // to happen. 4000 characters is comfortably inside it and far below the distance to any
        // other part of the file.
        Assert.True(Math.Abs(coverage - countOut) < 4000,
            $"the two refusals must live in the same fan-out guard block; they are {Math.Abs(coverage - countOut)} chars apart, "
            + "which suggests the coverage one drifted to parse time where it would wrongly refuse a single-bundle --jobs run");
    }

    /// <summary>
    /// It must refuse on `--coverage` itself, not only on an explicit `--coverage-out`. The
    /// default output path collides exactly the same way, and #3966 names covering both as an
    /// acceptance criterion.
    /// </summary>
    [Fact]
    public void TheRefusal_KeysOnCoverageBeingEnabled_NotOnAnExplicitOutputPath()
    {
        var src = ProgramSource();

        var idx = src.IndexOf("--coverage cannot be combined with a --jobs fan-out", StringComparison.Ordinal);
        Assert.True(idx > 0);

        // Walk back to the `if (...)` that guards the message and read its condition.
        var before = src.Substring(Math.Max(0, idx - 400), Math.Min(400, idx));
        var guard = Regex.Matches(before, @"if \(([^)]*)\)").Select(m => m.Groups[1].Value).LastOrDefault();

        Assert.False(string.IsNullOrWhiteSpace(guard), "expected an `if (...)` guarding the refusal");
        Assert.Contains("coverageEnabled", guard!);
        Assert.DoesNotContain("coverageOutputPath != null", guard!);
    }
}
