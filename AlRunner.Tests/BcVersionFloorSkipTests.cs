// BcVersionFloorSkipTests — a suite that declares a minimum BC version above the one under
// test must be SKIPPED (loudly), not dragged through a compile that cannot succeed.
//
// Why this exists: app.json's `application` / `platform` fields are MINIMA in AL, not pins.
// The runner used to ignore them, so a suite needing a newer BC failed by way of its Microsoft
// dependencies silently not resolving — the module was dropped from emit with no AL diagnostic
// attached, which reads as a runner bug rather than a declared incompatibility. That is what
// made all three BC 27.x matrix legs abort before running a single test: one suite depending on
// Microsoft/Application Test Library, an app that only exists from BC 28.0 onwards.
//
// The fixture pins the behaviour to a floor no artifact will ever satisfy (99.0.0.0), so it is
// deterministic regardless of which BC version the host has provisioned.

using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

// See DefineFlagIntegrationTests for why runner-subprocess tests used to be
// [Collection("server-serial")] and no longer are — #1809.
public sealed class BcVersionFloorSkipTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string BundlePath = Path.Combine(
        RepoRoot, "AlRunner.Tests", "Fixtures", "BcFloorSkip");

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
    /// The out-of-range suite is skipped and says so; its sibling still runs; the run is green.
    ///
    /// Not vacuous in either direction. The future suite's only test fails unconditionally, so a
    /// regressed floor check makes the run red — either because the test executed, or because
    /// the suite failed to emit (a suite error, which has reached the exit code since 6fbb6ff1).
    /// And asserting the sibling PASSED rules out the opposite failure: a runner that skipped the
    /// entire bundle would exit 0 while covering nothing at all.
    /// </summary>
    [SkippableFact]
    public void SuiteDeclaringANewerBc_IsSkippedWhileItsSiblingStillRuns()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner(BundlePath);

        // Skipped, and legible: the suite name, the floor it declares, and the reason.
        Assert.Contains("[skip]", output, StringComparison.Ordinal);
        Assert.Contains("BC Floor Skip - Future", output, StringComparison.Ordinal);
        Assert.Contains("99.0.0.0", output, StringComparison.Ordinal);

        // The failing test inside the skipped suite never ran...
        Assert.DoesNotContain("BcFloorSkip_FutureSuite_MustNeverExecute", output, StringComparison.Ordinal);

        // ...while the sibling that declares a satisfiable floor did.
        Assert.Contains("BcFloorSkip_HealthySibling_StillRuns", output, StringComparison.Ordinal);
        Assert.Contains("1P/0F/0E", output, StringComparison.Ordinal);

        Assert.True(exit == 0,
            $"a suite skipped for a declared BC floor is not a failure — the run must be green. exit={exit}\n{output}");
    }

    /// <summary>
    /// Negative direction on the comparison itself: a floor at or below the running BC must NOT
    /// skip. Without this, `ReadMinimumBcVersion` returning "always skip" (or BuildAppGroups
    /// dropping every suite carrying a floor) would still satisfy the test above.
    /// </summary>
    [SkippableFact]
    public void SuiteDeclaringASatisfiableFloor_IsNotSkipped()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner(Path.Combine(BundlePath, "healthy-suite"));

        Assert.True(exit == 0, $"the healthy suite alone must pass. exit={exit}\n{output}");
        Assert.Contains("BcFloorSkip_HealthySibling_StillRuns", output, StringComparison.Ordinal);
        Assert.DoesNotContain("[skip]", output, StringComparison.Ordinal);
    }
}
