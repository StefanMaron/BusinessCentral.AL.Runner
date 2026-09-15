// #4071 review: naming a value type that carries a BC CodeAnalysis field (BcCompiler's
// ManifestCompilerInputs) from Program's Main body, or as a static field type of a class touched
// at startup, makes the JIT load Microsoft.Dynamics.Nav.CodeAnalysis before --bc-version is parsed.
// That load picks the NEWEST provisioned version, and the explicit selection then refuses with
// "BC version already selected (<newest>); cannot re-select <requested>". CI provisions one
// version per leg, so only a machine with two versions sees it; this test builds that machine.
using System.Diagnostics;
using System.Text;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class OlderBcVersionSelectionWithNewerProvisionedTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string MinimalBundle =
        Path.Combine(RepoRoot, "tests", "runner-extras", "esm-xapp-table");

    private static (int ExitCode, string Output) Run(string artifactsRoot, string cacheDir, params string[] args)
    {
        var sb = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
        foreach (var a in args) sb.Append(' ').Append(a);
        sb.Append($" --cache \"{cacheDir}\" \"{MinimalBundle}\"");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = sb.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        psi.Environment["AL_RUNNER_ARTIFACTS_ROOT"] = artifactsRoot;
        psi.Environment.Remove("AL_RUNNER_VERBOSE");
        var output = new StringBuilder();
        using var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(180_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("al-runner did not exit within 180s");
        }
        proc.WaitForExit();
        lock (output) return (proc.ExitCode, output.ToString());
    }

    [SkippableFact]
    public void ExplicitEngineVersion_WithANewerVersionProvisioned_SelectsTheRequestedOne()
    {
        var engineVersion = BcArtifacts.EngineBuiltVersion();
        TestArtifacts.SkipIf(engineVersion == null, "no baked-in BcEngineVersion on this build.");
        var realHome = TestArtifacts.HomeDir()
            ?? throw new InvalidOperationException("Cannot determine this machine's HOME.");
        var realEngineDir = Path.Combine(TestArtifacts.StandardCacheDir(realHome), engineVersion!.ToString());
        TestArtifacts.SkipIfDirectoryMissing(realEngineDir, $"BC {engineVersion} artifacts");

        // Two provisioned versions: the engine's own, and a newer alias of the same files (+50 on
        // the minor keeps it clear of every real BC minor).
        var root = TestScratch.Dir("al-runner-older-bc-version-selection");
        Directory.CreateDirectory(root);
        var newer = new Version(engineVersion.Major, engineVersion.Minor + 50, engineVersion.Build, engineVersion.Revision);
        Directory.CreateSymbolicLink(Path.Combine(root, engineVersion.ToString()), realEngineDir);
        Directory.CreateSymbolicLink(Path.Combine(root, newer.ToString()), realEngineDir);
        var cacheDir = TestScratch.Dir("al-runner-older-bc-version-selection-cache");
        try
        {
            var (exit, output) = Run(root, cacheDir, "--no-auto-provision", "--bc-version", engineVersion.ToString());

            Assert.DoesNotContain("BC version already selected", output, StringComparison.Ordinal);
            Assert.Contains($"[bc] selected BC {engineVersion} (", output, StringComparison.Ordinal);
            Assert.True(exit == 0, $"expected a clean run with the older version selected. exit={exit}\n{output}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }
}
