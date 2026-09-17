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

using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class CoverageFanOutRefusalTests
{
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
