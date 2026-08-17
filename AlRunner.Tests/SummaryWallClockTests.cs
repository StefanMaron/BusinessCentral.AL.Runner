// SummaryWallClockTests — #1936: the plain-text summary's `total:` line is only
// AL-emit + C#-compile + test-run seconds. It does NOT include the fixed
// per-process costs paid before any of those phases start (BC runtime patch
// application, package-cache indexing, install-seed-dep company baseline, …).
// A warm run of a single-test fixture can print "total: 6.3s" while the process
// actually took ~23s wall clock — a number that reads as a lie to anyone timing
// the CLI from the outside (see COMMON.md's boot-overhead profile).
//
// This adds a `wall:` line — real OS-process wall-clock, start to summary print —
// printed right after `total:`, and a `wallSeconds` field on --output-json's JSON
// summary. Both are strictly additive: `total:`'s existing value/semantics and
// every existing JSON field are unchanged (proven by the exact "total:" and
// existing-field assertions below, not just presence of the new one).
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class SummaryWallClockTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string Fixture =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "RecordTriggerXRec");

    /// <summary>
    /// Positive: the plain-text summary prints a `wall:` line, in the same
    /// "  label:       N.Ns" style as the existing `total:` line right above it,
    /// whose value is both &gt; 0 (a real duration was measured, not a zeroed
    /// stub) and &gt;= `total:`'s own value (wall clock covers strictly more than
    /// the three phases `total:` sums — it can never read LOWER than their sum).
    /// A gutted implementation that always printed "wall:        0.0s" would fail
    /// the &gt; 0 half; one that echoed `total:`'s own value under a new label
    /// would still fail the &gt;= comparison here for any fixture with non-zero
    /// fixed boot cost, which this fixture reliably has (BC runtime patch
    /// application alone is multiple seconds — see COMMON.md).
    /// </summary>
    [SkippableFact]
    public void PlainTextSummary_PrintsWallLine_AtLeastAsLargeAsTotal()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = Run($"{TestBuildConfig.BcVersionArg} \"{Fixture}\"");
        Assert.Equal(0, exit);

        var totalMatch = Regex.Match(output, @"total:\s+([\d.]+)s");
        Assert.True(totalMatch.Success, $"expected a 'total:' line in output:\n{output}");
        var total = double.Parse(totalMatch.Groups[1].Value);

        var wallMatch = Regex.Match(output, @"wall:\s+([\d.]+)s");
        Assert.True(wallMatch.Success, $"expected a 'wall:' line in output:\n{output}");
        var wall = double.Parse(wallMatch.Groups[1].Value);

        Assert.True(wall > 0, $"expected wall: > 0, got {wall}");
        Assert.True(wall >= total,
            $"expected wall: ({wall}) >= total: ({total}) — wall clock covers strictly " +
            $"more than the emit+compile+run phases total: sums.\n{output}");

        // wall: must appear directly after total: (same block, same formatting style),
        // not floating somewhere unrelated in the output.
        Assert.True(wallMatch.Index > totalMatch.Index,
            "expected 'wall:' to appear after 'total:' in the summary block");
    }

    /// <summary>
    /// Positive: --output-json carries the same real duration as a numeric
    /// `wallSeconds` field, additive to the existing schema — every field the
    /// JSON output already carried (exitCode, total, passed, failed) is asserted
    /// unchanged alongside it, so this cannot pass against a change that broke
    /// the existing shape while bolting the new field on.
    /// </summary>
    [SkippableFact]
    public void JsonSummary_CarriesWallSecondsField_Numeric()
    {
        TestArtifacts.SkipIfMissing();

        // stdout-only: --output-json's contract is "stdout is JSON-only" (see
        // OutputFormatTests) — [bc]/[cache] banner lines go to stderr, so mixing
        // the two streams into one buffer would break JSON parsing.
        var (output, exit) = RunStdoutOnly($"{TestBuildConfig.BcVersionArg} --output-json \"{Fixture}\"");
        Assert.Equal(0, exit);

        using var doc = JsonDocument.Parse(output.Trim());
        var root = doc.RootElement;

        // Existing fields still present and correct — proves the schema stayed additive.
        Assert.Equal(0, root.GetProperty("exitCode").GetInt32());
        Assert.Equal(1, root.GetProperty("total").GetInt32());
        Assert.Equal(1, root.GetProperty("passed").GetInt32());
        Assert.Equal(0, root.GetProperty("failed").GetInt32());

        Assert.True(root.TryGetProperty("wallSeconds", out var wallSecondsProp),
            $"expected a numeric 'wallSeconds' field in JSON output:\n{output}");
        Assert.Equal(JsonValueKind.Number, wallSecondsProp.ValueKind);
        var wallSeconds = wallSecondsProp.GetDouble();
        Assert.True(wallSeconds > 0, $"expected wallSeconds > 0, got {wallSeconds}");
    }

    private static (string output, int exit) Run(string runnerArgs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = TestBuildConfig.RunArgs(ProjectPath) + " " + runnerArgs,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(240_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    private static (string output, int exit) RunStdoutOnly(string runnerArgs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = TestBuildConfig.RunArgs(ProjectPath) + " " + runnerArgs,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        // Keep stderr separate — see OutputFormatTests' identical rationale.
        p.ErrorDataReceived += (_, __) => { };
        p.BeginErrorReadLine();
        if (!p.WaitForExit(240_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }
}
