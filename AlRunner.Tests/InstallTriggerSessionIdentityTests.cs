// InstallTriggerSessionIdentityTests — AlRunner#3268.
//
// RUNNER-MECHANISM tests. Nothing here is a claim about BC: real Business Central has no
// "adoption" and no session-user seed, because the session user is a row in the database long
// before any extension is installed. What these pin is the runner's own install-seed ORDER —
// that the session identity install code observes is the identity the tests then observe.
//
// THE DEFECT (#3268)
//   TestExecutor called RecordPatches.EnsureUserSystemTableRowSeeded AFTER
//   InstallTriggerRunner.RunTestAssemblyOnly, so the #2983 adoption decision was made once the
//   bundle's install code had already run. Install code that stored UserSecurityId() therefore
//   stored the runner-GENERATED id, and the seed then moved the session onto an adopted row —
//   leaving every row keyed on the stored id pointing at a user the session is not, with a
//   TableRelation that resolves to the wrong row or to none.
//
// THE RED THESE TESTS ENCODE, measured on this fixture against the pre-fix runner:
//   3P/2F cold and 3P/2F warm.
//     FAIL ItsiInstallCodeSawTheIdentityTheTestsSee — "install code stored
//          {C0A1BDFA-0000-0000-0000-545553545553} as the owner, but UserSecurityId() is now
//          {D41F7A96-2C58-4E13-8B0A-7F5C9E62D3A4}"
//     FAIL ItsiTheStoredOwnerIsTheAdoptedIdNotTheGeneratedOne
//   The two preconditions (the stand-in row is present; the session did adopt) PASS in both
//   arms, which is what says the failure is about ORDER and not about the fixture failing to
//   arrange the collision.
//
// WHY COLD AND WARM
//   The row the session adopts is written by a DEPENDENCY install trigger, and dependency
//   install triggers run inside the #1867 dep-company baseline window. On a warm process that
//   window is a DISK-HIT: the triggers do not run at all and the row arrives from the restored
//   snapshot instead. Both arms therefore have to be measured (.claude/rules/local-test-scope.md
//   — a cache-sensitive change needs the second run), and the warm arm asserts the DISK-HIT
//   marker, because "warm" is not measured unless the run actually took the cached path.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class InstallTriggerSessionIdentityTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <summary>The bundle handed to the runner. Its seed app is the sibling <c>dep</c> dir,
    /// resolved by BuildSiblingSourceDeps.</summary>
    private static string BundleDir => Path.Combine(
        RepoRoot, "AlRunner.Tests", "Fixtures", "InstallTriggerSessionIdentity", "main");

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
        // The ordering claim itself, asserted by NAME so a green cannot come from the fixture
        // having quietly stopped arranging the collision.
        Assert.Contains("PASS  Codeunit70782.ItsiTheDependencySeededTheSameNamedUser", stdout);
        Assert.Contains("PASS  Codeunit70782.ItsiTheSessionAdoptedTheDependencysSecurityId", stdout);
        Assert.Contains("PASS  Codeunit70782.ItsiInstallCodeSawTheIdentityTheTestsSee", stdout);
        Assert.Contains("PASS  Codeunit70782.ItsiTheStoredOwnerIsTheAdoptedIdNotTheGeneratedOne", stdout);
        Assert.Contains("PASS  Codeunit70782.ItsiTheStoredOwnerNameIsUnchangedByAdoption", stdout);
        Assert.True(exit == 0,
            $"{arm}: expected a clean run. exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
    }

    /// <summary>
    /// COLD, then WARM in a second process against the same cache root — the two arms of
    /// .claude/rules/local-test-scope.md's cache-sensitive rule, in one test because the warm
    /// arm is only meaningful after the cold one has written the entry it hits.
    /// </summary>
    [SkippableFact]
    public void InstallCodeSeesTheSessionIdentityTheTestsSee_ColdAndOnACachedDepBaseline()
    {
        TestArtifacts.SkipIfMissing();
        // TestScratch.Dir, not a hand-rolled temp combine: a --cache root is the expensive kind
        // and an OWNED directory is reclaimable by a later runner start (#2706/#2743).
        var cacheDir = TestScratch.Dir("al-runner-itsi");
        try
        {
            var cold = Run(cacheDir);
            AssertEveryTestPassed("cold", cold.ExitCode, cold.StdOut, cold.StdErr);
            // The dependency install triggers really ran this time — the arm the warm run below
            // is being contrasted with.
            Assert.Contains("InstallBaseline.DepCompanyCache MISS", cold.StdErr);

            // The adoption is still happening; #3268 moved WHEN it is decided, not whether. If
            // this line disappeared, all five AL tests could pass with both ids equal to the
            // generated one and the fixture would be measuring nothing.
            Assert.Contains("ADOPTED the security id", cold.StdErr);
            Assert.Contains("D41F7A96-2C58-4E13-8B0A-7F5C9E62D3A4", cold.StdErr);

            var warm = Run(cacheDir);
            AssertEveryTestPassed("warm", warm.ExitCode, warm.StdOut, warm.StdErr);
            // WITHOUT this the warm arm is not warm. On a DISK-HIT the dependency install
            // triggers do not run, so the row the session adopts comes out of the restored
            // baseline — which is the path that would break if the seed were moved inside the
            // cached window, where a HIT restores the row but cannot re-make the decision.
            Assert.Contains("InstallBaseline.DepCompanyCache DISK-HIT", warm.StdErr);
            Assert.DoesNotContain("InstallBaseline.DepCompanyCache MISS", warm.StdErr);
            Assert.Contains("ADOPTED the security id", warm.StdErr);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }
}
