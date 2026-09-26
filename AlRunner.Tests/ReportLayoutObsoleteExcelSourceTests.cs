// ReportLayoutObsoleteExcelSourceTests — #4571: "Report Layout List" rows for a report the runner
// COMPILES carry the layout's ObsoleteState and ExcelLayoutMultipleDataSheets, both from the
// compiler's ReportLayoutSymbol (cold) and from the report-layout sidecar replayed on an AL-output
// cache HIT (warm). The BC-behaviour claim is pinned by the corpus; this pins the runner's capture
// and its cache replay, which a single cold run cannot reach.

using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public class ReportLayoutObsoleteExcelSourceTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string FixtureRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "ReportLayoutObsoleteExcelSource"));

    private static readonly string[] Tests =
    {
        "PendingObsoleteLayoutIsObsolete",
        "LayoutWithoutObsoleteStateIsNotObsolete",
        "MultipleDataSheetsTrueIsMultiple",
        "MultipleDataSheetsFalseIsSingle",
        "UndeclaredSheetPropertyIsDefault",
    };

    private static (string output, int exit) RunRunner(string bundleDir, string cacheDir)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{bundleDir}\" --cache \"{cacheDir}\" --verbose");
        var platformApps = TestArtifacts.PlatformAppsDir();
        if (Directory.Exists(platformApps))
            args.Append($" --package-cache \"{platformApps}\"");
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
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    private static void AssertAllPass(string output, int exit, string phase)
    {
        foreach (var t in Tests)
        {
            Assert.True(output.Contains($"Codeunit71863.{t}") || output.Contains($"\"RLO Tests\".{t}"),
                $"{phase}: test {t} did not run.\n{output}");
            Assert.False(RunnerFailureLines.Failed(output, 71863, t), $"{phase}: {t} failed.\n{output}");
        }
        Assert.True(output.Contains("passed 5   failed 0   errors 0"), $"{phase}: expected 5 passes.\n{output}");
        Assert.Equal(0, exit);
    }

    [SkippableFact]
    public void SourceCompiledLayoutRows_CarryObsoleteStateAndSheetConfiguration_ColdThenWarm()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = TestScratch.Dir("al-runner-report-layout-obsolete-excel-source");
        Directory.CreateDirectory(bundle);
        foreach (var f in Directory.GetFiles(FixtureRoot))
            File.Copy(f, Path.Combine(bundle, Path.GetFileName(f)), overwrite: true);
        var cacheDir = TestScratch.Dir("al-runner-report-layout-obsolete-excel-source-cache");

        var (cold, coldExit) = RunRunner(bundle, cacheDir);
        Assert.Contains("[cache] MISS", cold);
        AssertAllPass(cold, coldExit, "cold");

        // The warm run replays the layouts from the sidecar; BC's Emit does not run.
        var (warm, warmExit) = RunRunner(bundle, cacheDir);
        Assert.Contains("[cache] HIT", warm);
        AssertAllPass(warm, warmExit, "warm");
    }
}
