// PageOnInitTriggerTests — issue #4114. A RUNNER-MECHANISM test: what BC does is settled
// upstream by corpus codeunit 60488 "POI Tests"; this pins the runner's own dispatch of OnInit
// at page construction on the paths it drives, plus an Error() raised in OnInit reaching the
// test through the runner's construction catch sites.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class PageOnInitTriggerTests
{
    /// <summary>The cap this file's subprocess spawns actually apply, and the single source of
    /// the figure their timeout messages report (#4275). Derived rather than repeated: a literal
    /// in the message is invisible while it happens to match, and wrong the moment the cap moves.
    /// Measured for real on #3435 — a cap squeezed to 3s still threw "did not exit within 120s".
    /// Same shape as BcVersionDefaultDocumentationTests.SpawnTimeoutMs (#3487).</summary>
    private const int SpawnTimeoutMs = 180_000;

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "PageOnInitTrigger");

    private static (int ExitCode, string StdOut, string StdErr) Run(string cacheDir)
    {
        var sb = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
        sb.Append(' ').Append($"\"{FixtureDir}\"");
        sb.Append(' ').Append($"--cache \"{cacheDir}\"");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = sb.ToString(),
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
        if (!proc.WaitForExit(SpawnTimeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"al-runner did not exit within {SpawnTimeoutMs / 1000}s.");
        }
        // WaitForExit(int) does not drain the async output callbacks; the parameterless
        // overload does. See #2496.
        proc.WaitForExit();
        return (proc.ExitCode, outSb.ToString(), errSb.ToString());
    }

    [Fact]
    public void OnInit_RunsOnceBeforeOnOpenPage_OnEveryOpenPath()
    {
        var cacheDir = TestScratch.Dir("al-runner-pit-tests");
        try
        {
            var (exit, stdout, stderr) = Run(cacheDir);

            Assert.True(exit == 0,
                $"every fixture test must pass. exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");

            Assert.Contains("PASS  Codeunit71903.OpenEdit_RunsOnInitOnceBeforeOnOpenPage", stdout);
            Assert.Contains("PASS  Codeunit71903.ActionBoundToOnInitGlobals_EnabledAndInvokeFollowThem", stdout);
            Assert.Contains("PASS  Codeunit71903.Reopen_RunsOnInitAgain", stdout);
            Assert.Contains("PASS  Codeunit71903.RunModal_RunsOnInitOnce", stdout);
            Assert.Contains("PASS  Codeunit71903.PageRun_RunsOnInitOnce", stdout);
            Assert.Contains("PASS  Codeunit71903.SetterBeforeRunModal_RunsAfterOnInit", stdout);
            Assert.Contains("PASS  Codeunit71903.ErrorInOnInit_ReachesTheTest", stdout);
            Assert.DoesNotContain("FAIL", stdout);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
