// InstallExecutionContextTests — AlRunner#4049.
//
// RUNNER-MECHANISM test. The BC claim (an install trigger sees ExecutionContext::Install) is
// adjudicated by corpus codeunit 60589 TestInstallExecCtx_*. What this pins is the runner's own
// scope around InstallTriggerRunner.FireAll: InstallExecutionContext sets BC's
// NavSession.AppInstallationContext for the installing app, the GetCurrentModuleExecutionContext
// rewrite finds that app by stack walk, and the scope is cleared before any test runs.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class InstallExecutionContextTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string BundleDir => Path.Combine(
        RepoRoot, "AlRunner.Tests", "Fixtures", "InstallExecutionContext");

    private static string LoadPackageDataBundleDir => Path.Combine(
        RepoRoot, "AlRunner.Tests", "Fixtures", "InstallLoadPackageData");

    private static (int ExitCode, string StdOut, string StdErr) Run(string cacheDir) => Run(cacheDir, BundleDir);

    private static (int ExitCode, string StdOut, string StdErr) Run(string cacheDir, string bundleDir)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" --package-cache \"").Append(TestArtifacts.PlatformAppsDir()).Append('"');
        args.Append(' ').Append($"\"{bundleDir}\"");
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
        // WaitForExit(int) returns without draining the async read callbacks (#2496).
        proc.WaitForExit();
        return (proc.ExitCode, outSb.ToString(), errSb.ToString());
    }

    [SkippableFact]
    public void InstallContextIsSetForTheInstallingAppAndClearedBeforeTests()
    {
        TestArtifacts.SkipIfMissing();
        var cacheDir = TestScratch.Dir("al-runner-iec");
        try
        {
            var (exit, stdout, stderr) = Run(cacheDir);
            // By name: the three halves fail independently — no Set, no module stack walk, no Clear.
            Assert.Contains("PASS  Codeunit70902.IecInstallTriggerSawInstall", stdout);
            Assert.Contains("PASS  Codeunit70902.IecInstallTriggerModuleSawInstall", stdout);
            Assert.Contains("PASS  Codeunit70902.IecContextIsClearedAfterThePass", stdout);
            // #3292 guard on BC's real field: StartSession inside the install trigger was refused.
            Assert.Contains("PASS  Codeunit70902.IecStartSessionInsideInstallWasRefused", stdout);
            // BC's early return outside install is kept by the #4061 patch.
            Assert.Contains("PASS  Codeunit70902.IecLoadPackageDataOutsideInstallReturns", stdout);
            Assert.True(exit == 0, $"expected a clean run. exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// #4061: with the install context set, NavApp.LoadPackageData inside an install trigger
    /// reaches package-data import, which the runner does not implement. It must refuse naming
    /// the API and the reason. The fixture calls it through a [TryFunction]; a not-yet-implemented
    /// refusal deliberately tears through one (TryFunctionOutOfScopeTrapTests), so the install
    /// pass fails and the bundle reports EXEC-FAIL rather than a TryFunction answering false.
    /// </summary>
    [SkippableFact]
    public void LoadPackageDataInsideInstallTrigger_RefusesNamingTheApiAndReason()
    {
        TestArtifacts.SkipIfMissing();
        var cacheDir = TestScratch.Dir("al-runner-ilp");
        try
        {
            var (exit, stdout, stderr) = Run(cacheDir, LoadPackageDataBundleDir);
            var all = stdout + stderr;
            Assert.NotEqual(0, exit);
            Assert.Contains("RunnerOutOfScopeException: out-of-scope: NavApp.LoadPackageData — not-yet-implemented", all);
            Assert.Contains("(#4061)", all);
            Assert.DoesNotContain("Index (zero based)", all);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }
}
