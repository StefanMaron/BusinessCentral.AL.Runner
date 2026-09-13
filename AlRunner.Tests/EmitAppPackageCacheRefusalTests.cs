using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #4032: `--emit-app` only packages app.json and the *.al sources, so it reads no
/// package cache. It used to parse `--package-cache PATH` and drop it, with no message.
/// It now refuses any argument after its two positionals with exit 2, naming the argument.
///
/// Both tests use a bundle directory that does not exist, so the run ends at the
/// identity read with exit 2 unless the argument guard fires first. The two cases share
/// the exit code and differ only in the message, which is what the assertions read.
/// </summary>
public sealed class EmitAppPackageCacheRefusalTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        psi.Arguments = TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner"));
        foreach (var a in args) psi.Arguments += $" \"{a}\"";
        // Drain both pipes asynchronously: see CliDocumentationTests.RunCli for the deadlock.
        using var proc = Process.Start(psi)!;
        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (outSb) outSb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errSb) errSb.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(240_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"al-runner did not exit within 240s for args: {string.Join(' ', args)}");
        }
        proc.WaitForExit();
        lock (outSb) lock (errSb) return (proc.ExitCode, outSb.ToString(), errSb.ToString());
    }

    private static string MissingBundle() =>
        Path.Combine(TestScratch.Dir("emit-app-4032"), "missing-bundle");

    [Fact]
    public void EmitApp_WithPackageCache_ExitsTwoNamingTheFlag()
    {
        var bundle = MissingBundle();
        var (exit, _, stderr) = RunCli("--emit-app", bundle, bundle + ".app", "--package-cache", "/some/cache");

        Assert.Equal(2, exit);
        Assert.Contains("--emit-app: --package-cache has no effect", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("could not read identity", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void EmitApp_WithUnknownTrailingArgument_ExitsTwoNamingIt()
    {
        var bundle = MissingBundle();
        var (exit, _, stderr) = RunCli("--emit-app", bundle, bundle + ".app", "--bogus-flag");

        Assert.Equal(2, exit);
        Assert.Contains("--emit-app: unexpected argument '--bogus-flag'", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("could not read identity", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void EmitApp_WithOnlyPositionals_IsNotRefusedByTheArgumentGuard()
    {
        var bundle = MissingBundle();
        var (exit, _, stderr) = RunCli("--emit-app", bundle, bundle + ".app");

        Assert.Equal(2, exit);
        Assert.Contains("--emit-app: could not read identity", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("has no effect", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("unexpected argument", stderr, StringComparison.Ordinal);
    }
}
