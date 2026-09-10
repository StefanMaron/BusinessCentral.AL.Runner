// DepInstallTriggerSessionIdentityTests — AlRunner#3698.
//
// RUNNER-MECHANISM tests. Nothing here is a claim about BC: real Business Central has no
// session-user seed at all, because the session user is a row in the database long before any
// extension is installed. What these pin is the runner's own install-seed ORDER, one layer out
// from #3268: the identity a DEPENDENCY's install code observes is the identity the tests then
// observe, and the session user is already a row in User (2000000120) while that code runs.
//
// THE DEFECT (#3698)
//   TestExecutor seeded the User row AFTER the #1867 dep-company baseline window, and the
//   dependency apps' OnInstallAppPerCompany triggers run INSIDE it. So a dependency's install
//   code looked the session user up in an empty User table — every TableRelation to
//   User."User Security ID" it wrote refused the id UserSecurityId() itself returned — and the
//   seed could afterwards move the session onto an adopted row, leaving anything the dependency
//   had keyed on that id naming a user the session is not.
//
// THE RED THESE TESTS ENCODE, measured on this fixture against the pre-fix runner:
//   4P/1F cold and 4P/1F warm.
//     FAIL Codeunit70820.DisiDependencyInstallCodeSawTheSessionUsersOwnRow — "dependency install
//          code looked up UserSecurityId() in User (2000000120) and found no row"
//   #3757 added four more, of which two are RED against the pre-#3757 runner in both arms:
//     FAIL Codeunit70820.DisiDependencyInstallCodeSawTheCompanyRow — "dependency install code
//          called Company.Get(CompanyName()) and found no row"
//     FAIL Codeunit70820.DisiDependencyInstallCodeSawTheSuperGrant — "dependency install code
//          found no SUPER row in Access Control (2000000053) for UserSecurityId()"
//   The other two are the controls that keep those two honest: the key-consulting negative for
//   Company, and the registry pair — the dependency's OWN row present, the BUNDLE's absent —
//   which is the invariant the fix must NOT break, since the bundle's Published Application row
//   is deliberately seeded outside the shared dependency snapshot.
//
//   DisiTheSeedWroteExactlyOneRowForTheSessionUser passes in both arms before AND after: it is
//   here because the FIX introduces a second seed call, and a second INSERT is the way that fix
//   goes wrong.
//
// WHY COLD AND WARM
//   The observation row is written by a DEPENDENCY install trigger, so it lands inside the
//   #1867 window and is part of the captured snapshot. On a warm process that window is a
//   DISK-HIT: the trigger does not run at all and the row arrives from the restored snapshot,
//   which is the path a fix that only reordered the MISS branch would leave broken
//   (.claude/rules/local-test-scope.md — a cache-sensitive change needs the second run).
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class DepInstallTriggerSessionIdentityTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <summary>The bundle handed to the runner. Its observer app is the sibling <c>dep</c> dir,
    /// resolved by BuildSiblingSourceDeps.</summary>
    private static string BundleDir => Path.Combine(
        RepoRoot, "AlRunner.Tests", "Fixtures", "DepInstallTriggerSessionIdentity", "main");

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
        // running its dependency install trigger.
        Assert.Contains("PASS  Codeunit70820.DisiTheDependencyRecordedWhatItSaw", stdout);
        Assert.Contains("PASS  Codeunit70820.DisiDependencyInstallCodeSawTheSessionUsersOwnRow", stdout);
        Assert.Contains("PASS  Codeunit70820.DisiTheDependencySawTheIdentityTheTestsSee", stdout);
        Assert.Contains("PASS  Codeunit70820.DisiTheDependencysLookupConsultedTheKey", stdout);
        Assert.Contains("PASS  Codeunit70820.DisiTheSeedWroteExactlyOneRowForTheSessionUser", stdout);
        // #3757 — the three sibling seeds, measured from the same observation row.
        Assert.Contains("PASS  Codeunit70820.DisiDependencyInstallCodeSawTheCompanyRow", stdout);
        Assert.Contains("PASS  Codeunit70820.DisiTheDependencysCompanyLookupConsultedTheKey", stdout);
        Assert.Contains("PASS  Codeunit70820.DisiDependencyInstallCodeSawTheSuperGrant", stdout);
        Assert.Contains("PASS  Codeunit70820.DisiTheDependencySawItsOwnAppInstalledAndNotTheBundle", stdout);
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
    public void DependencyInstallCodeSeesTheSeededSessionUser_ColdAndOnACachedDepBaseline()
    {
        TestArtifacts.SkipIfMissing();
        // TestScratch.Dir, not a hand-rolled temp combine: a --cache root is the expensive kind
        // and an OWNED directory is reclaimable by a later runner start (#2706/#2743).
        var cacheDir = TestScratch.Dir("al-runner-disi");
        try
        {
            var cold = Run(cacheDir);
            AssertEveryTestPassed("cold", cold.ExitCode, cold.StdOut, cold.StdErr);
            // The dependency install trigger really ran this time — the arm the warm run below
            // is being contrasted with.
            Assert.Contains("InstallBaseline.DepCompanyCache MISS", cold.StdErr);
            // NEGATIVE CONTROL. This fixture arranges no collision, so nothing may be adopted;
            // an implementation that reached the settled identity by adopting some row would
            // satisfy the AL assertions and be wrong.
            Assert.DoesNotContain("ADOPTED the security id", cold.StdErr);

            var warm = Run(cacheDir);
            AssertEveryTestPassed("warm", warm.ExitCode, warm.StdOut, warm.StdErr);
            // WITHOUT this the warm arm is not warm. On a DISK-HIT the dependency install
            // trigger does not run, so the observation row comes out of the restored baseline —
            // which is the half of the split a MISS-only fix would leave broken.
            Assert.Contains("InstallBaseline.DepCompanyCache DISK-HIT", warm.StdErr);
            Assert.DoesNotContain("InstallBaseline.DepCompanyCache MISS", warm.StdErr);
            Assert.DoesNotContain("ADOPTED the security id", warm.StdErr);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }
}
