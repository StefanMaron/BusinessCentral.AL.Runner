// BundleSuiteErrorLoudnessTests — a suite that fails to emit must fail the run even when
// its SIBLING suites in the same bundle still produce passing tests.
//
// EmitExclusionLoudnessTests already covers the single-suite shape, and it passed — but it
// passed for a reason that does not generalise. On exclusion, Program.cs empties the broken
// module's sources ("do not run a module that is missing objects"), so in a ONE-suite bundle
// zero tests survive. The bundle-level guard was:
//
//     if (bundleTests.Count == 0 && bundleErrors.Count > 0) bundleStage = CompileFailed;
//
// i.e. it required the bundle to come out EMPTY. One healthy sibling suite is enough to keep
// bundleTests non-empty, and then every suite error becomes invisible to the exit code, which
// is computed from bucket Stage + per-test outcomes only.
//
// That is the canonical mode for this repo (bundled per-bucket runs), so the loudness
// guarantee was absent exactly where it matters most. Measured on the real matrix: BC 27.0 ran
// 26 of ~76 runner-extras tests with 16 suite errors and exited 0, and BC 28.0 ran 8 of 76 with
// 14 suite errors and exited non-zero ONLY because one surviving test happened to fail. Had it
// passed, a leg that compiled almost nothing would have reported green.
//
// See .claude/rules/loud-failures.md — a green run that quietly covers less is the "green test
// that lies" this rule exists to prevent.

using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

// See DefineFlagIntegrationTests for why runner-subprocess tests are serialized.
[Collection("server-serial")]
public sealed class BundleSuiteErrorLoudnessTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string BundlePath = Path.Combine(
        RepoRoot, "AlRunner.Tests", "Fixtures", "EmitExclusionBundle");

    private static (string Output, int Exit) RunRunner(string target)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{target}\"");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
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

    /// <summary>
    /// The bundle pairs a suite whose only object cannot bind with a healthy sibling. The
    /// healthy test MUST still run (otherwise this proves nothing about siblings), and the
    /// run MUST still exit non-zero because a whole suite went missing.
    ///
    /// This test is not vacuous: before the fix it failed on the exit assertion alone, with
    /// the healthy test passing and "1 suite errors" printed — the run reporting success
    /// while silently covering one suite less than the bundle declares.
    /// </summary>
    [SkippableFact]
    public void SuiteErrorAlongsideAPassingSibling_StillFailsTheRun()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner(BundlePath);

        // The sibling really did run — without this, a runner that simply died on the whole
        // bundle would satisfy the exit assertion below while proving nothing.
        Assert.Contains("BundleHealthy_Addition_StillRuns", output, StringComparison.Ordinal);
        Assert.Contains("1P/0F/0E", output, StringComparison.Ordinal);

        // The suite error was detected and reported...
        Assert.Contains("EMIT-ZERO", output, StringComparison.Ordinal);
        Assert.Contains("1 suite errors", output, StringComparison.Ordinal);

        // ...and — the actual regression — it reached the EXIT CODE. Exit 3 is "compile
        // errors" per Program.cs's computedExitCode ladder.
        Assert.True(exit == 3,
            $"a suite that failed to emit means the bundle covers less than it declares, so the "
            + $"run must NOT exit 0 just because a sibling suite still passed. exit={exit}\n{output}");
    }

    /// <summary>
    /// Negative direction: the healthy suite ALONE must exit 0. Without this, the assertion
    /// above would still hold if the runner had started failing every bundle outright, which
    /// would be a far worse bug wearing the same green checkmark.
    /// </summary>
    [SkippableFact]
    public void HealthySuiteAlone_ExitsZero()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner(Path.Combine(BundlePath, "healthy-suite"));

        Assert.True(exit == 0, $"the healthy suite alone must pass. exit={exit}\n{output}");
        Assert.Contains("BundleHealthy_Addition_StillRuns", output, StringComparison.Ordinal);
        Assert.DoesNotContain("EMIT-ZERO", output, StringComparison.Ordinal);
        Assert.Contains("0 suite errors", output, StringComparison.Ordinal);
    }
}
