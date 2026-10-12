// WatchSiblingDependentCacheCliTests — issue #4125 without --watch: a warm AL-output cache must not serve the dependent's old compile.
using System.Diagnostics;
using Xunit;
using static AlRunner.Tests.WatchSiblingDependentStaleTests;

namespace AlRunner.Tests;

public class WatchSiblingDependentCacheCliTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    // The same stale answer without --watch: the dependent's AL-output cache key did not move when
    // only the sibling changed, so a warm --cache served the compile of the OLD surface.
    [SkippableFact]
    public void Cli_SiblingDependencyAloneChanges_AWarmCacheDoesNotServeTheDependentsOldCompile()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-cli-sibling-dependent-4125");
        var cacheDir = TestScratch.Dir("al-runner-cli-sibling-dependent-4125-cache");
        try
        {
            WriteApps(root);
            WriteDep(root, 58);
            WriteTests(root, 1);

            (int Exit, string Output) Run()
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg
                        + $" \"{root}\" --show-pass --verbose --cache \"{cacheDir}\"",
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
                };
                using var proc = Process.Start(psi)!;
                var stdout = proc.StandardOutput.ReadToEndAsync();
                var stderr = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(TimeSpan.FromMinutes(5)))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    proc.WaitForExit();
                    Assert.Fail($"the runner did not finish in 5 minutes.\n{stdout.Result}\n{stderr.Result}");
                }
                return (proc.ExitCode, stdout.Result + "\n" + stderr.Result);
            }

            var cold = Run();
            Assert.True(cold.Exit == 0 && cold.Output.Contains("PASS"), $"cold run did not pass (exit {cold.Exit}):\n{cold.Output}");

            // Unchanged, the ordinary warm start: both apps are served from the cache, so the sibling's
            // fingerprint is the same across processes (it must not carry anything per-process).
            var unchanged = Run();
            Assert.True(unchanged.Exit == 0, $"unchanged run did not pass (exit {unchanged.Exit}):\n{unchanged.Output}");
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(unchanged.Output, @"\[cache\] HIT ").Count);

            WriteDep(root, null);
            var warm = Run();
            Assert.DoesNotContain("Function ID", warm.Output);
            Assert.True(warm.Output.Contains("AL0132"), $"warm run did not report AL0132 (exit {warm.Exit}):\n{warm.Output}");
            Assert.Equal(3, warm.Exit);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }
}
