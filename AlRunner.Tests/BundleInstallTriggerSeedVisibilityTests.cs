// BundleInstallTriggerSeedVisibilityTests — AlRunner#3757.
//
// RUNNER-MECHANISM tests, the same shape as DepInstallTriggerSessionIdentityTests one layer in.
// Nothing here is a claim about BC's own install order: real Business Central has no seed stage
// at all, because the company, the app registry and the permission grants are database state
// long before an extension is installed. What this pins is the runner's own install-seed ORDER —
// the Company row (2000000006), the bundle's Published Application row (2000000206) and
// the session user's SUPER row in Access Control (2000000053) are
// all in place while the bundle's OWN install triggers run.
//
// THE DEFECT (#3757)
//   TestExecutor seeded all three AFTER InstallTriggerRunner.RunTestAssemblyOnly(), so a
//   bundle's OnInstallAppPerCompany saw a Company table with no row for the company it was
//   initialising, no Published Application row for itself — the shape System Application's own
//   module-ownership checks consult — and no Access Control row for a session user the runner
//   reports as SUPER everywhere else.
//
// THE RED THESE TESTS ENCODE, measured on this fixture against the pre-fix runner:
//   1P/3F cold and 1P/3F warm.
//     FAIL Codeunit70842.BisvInstallCodeSawTheCompanyRow
//          — "install code called Company.Get(CompanyName()) and found no row"
//     FAIL Codeunit70842.BisvInstallCodeSawTheSuperGrant
//          — "install code found no SUPER row in Access Control (2000000053)"
//     FAIL Codeunit70842.BisvInstallCodeSawItsOwnPublishedApplicationRow
//          — "install code found no Published Application row for its own app id"
//   BisvTheInstallTriggerRecordedWhatItSaw passes in both arms before AND after: it is the
//   precondition, and without it the three above would also pass on a run whose install trigger
//   never fired.
//
// WHY COLD AND WARM
//   Two of the three rows are seeded inside the #1867 dependency+company baseline window as well,
//   so they are part of the captured snapshot; on a warm process that window is a DISK-HIT and
//   the rows arrive from the restore instead. Both arms therefore have to be measured
//   (.claude/rules/local-test-scope.md — a cache-sensitive change needs the second run).
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class BundleInstallTriggerSeedVisibilityTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <summary>The bundle handed to the runner. It has no dependency app: the observation is
    /// made by the bundle's OWN install trigger.</summary>
    private static string BundleDir => Path.Combine(
        RepoRoot, "AlRunner.Tests", "Fixtures", "BundleInstallTriggerSeedVisibility", "main");

    private static (int ExitCode, string StdOut, string StdErr) Run(string cacheDir)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" --package-cache \"").Append(TestArtifacts.PlatformAppsDir()).Append('"');
        args.Append(' ').Append($"\"{BundleDir}\"");
        args.Append(' ').Append($"--cache \"{cacheDir}\"");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        // PERF is what puts the DepCompanyCache MISS/DISK-HIT markers on stderr; without it the
        // warm arm cannot say which tier answered.
        psi.Environment["AL_RUNNER_PERF"] = "1";

        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        using var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (outSb) outSb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errSb) errSb.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(300_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("al-runner did not exit within 300s.");
        }
        // WaitForExit(int) returns without draining the async read callbacks; only the
        // parameterless overload waits for those (#2496).
        proc.WaitForExit();
        return (proc.ExitCode, outSb.ToString(), errSb.ToString());
    }

    private static void AssertEveryTestPassed(string arm, int exit, string stdout, string stderr)
    {
        // Asserted by NAME so a green cannot come from the fixture having quietly stopped
        // running its install trigger.
        Assert.Contains("PASS  Codeunit70842.BisvTheInstallTriggerRecordedWhatItSaw", stdout);
        Assert.Contains("PASS  Codeunit70842.BisvInstallCodeSawTheCompanyRow", stdout);
        Assert.Contains("PASS  Codeunit70842.BisvInstallCodeSawTheSuperGrant", stdout);
        Assert.Contains("PASS  Codeunit70842.BisvInstallCodeSawItsOwnPublishedApplicationRow", stdout);
        Assert.DoesNotContain("FAIL", stdout);
        Assert.True(exit == 0,
            $"{arm}: expected a clean run. exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
    }

    /// <summary>
    /// COLD, then WARM in a second process against the same cache root — the two arms of
    /// .claude/rules/local-test-scope.md's cache-sensitive rule, in one test because the warm
    /// arm is only meaningful after the cold one has written the entry it hits.
    /// </summary>
    [SkippableFact]
    public void BundleInstallCodeSeesTheSeededCompanyAppAndSuperRows_ColdAndOnACachedDepBaseline()
    {
        TestArtifacts.SkipIfMissing();
        // TestScratch.Dir, not a hand-rolled temp combine: a --cache root is the expensive kind
        // and an OWNED directory is reclaimable by a later runner start (#2706/#2743).
        var cacheDir = TestScratch.Dir("al-runner-bisv");
        try
        {
            var cold = Run(cacheDir);
            AssertEveryTestPassed("cold", cold.ExitCode, cold.StdOut, cold.StdErr);
            Assert.Contains("InstallBaseline.DepCompanyCache MISS", cold.StdErr);
            // NEGATIVE CONTROL. This fixture arranges no User-name collision, so nothing may be
            // adopted; an implementation that reached the SUPER row by adopting some row would
            // satisfy the AL assertions and be wrong.
            Assert.DoesNotContain("ADOPTED the security id", cold.StdErr);
            // The seeds are loud when they cannot write (loud-failures.md); none of them may have
            // reported on a run this test calls green.
            Assert.DoesNotContain("[warn] CompanySystemTable:", cold.StdErr);
            Assert.DoesNotContain("[warn] AccessControlSeed:", cold.StdErr);
            Assert.DoesNotContain("[warn] PublishedApplication:", cold.StdErr);

            var warm = Run(cacheDir);
            AssertEveryTestPassed("warm", warm.ExitCode, warm.StdOut, warm.StdErr);
            // WITHOUT this the warm arm is not warm. On a DISK-HIT the Company row and the SUPER
            // row come out of the restored baseline rather than from the in-window seed, which is
            // the half of the split a MISS-only fix would leave broken.
            Assert.Contains("InstallBaseline.DepCompanyCache DISK-HIT", warm.StdErr);
            Assert.DoesNotContain("InstallBaseline.DepCompanyCache MISS", warm.StdErr);
            Assert.DoesNotContain("ADOPTED the security id", warm.StdErr);
            Assert.DoesNotContain("[warn] CompanySystemTable:", warm.StdErr);
            Assert.DoesNotContain("[warn] AccessControlSeed:", warm.StdErr);
            Assert.DoesNotContain("[warn] PublishedApplication:", warm.StdErr);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }
}
