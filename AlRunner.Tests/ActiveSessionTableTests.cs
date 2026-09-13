// ActiveSessionTableTests — issue #3233. RUNNER-MECHANISM: the runner seeds its own session's
// row in Active Session (2000000110), read back from the skeleton session. The BC-behaviour
// claim is adjudicated upstream by "Test Active Session Table" (corpus codeunit 60976).
//
// Runs the fixture TWICE against one cache root: the seed sits beside the install-baseline
// snapshot caches, and a seed that fires cold and is skipped on a HIT would stay green in CI,
// which provisions fresh every leg (.claude/rules/local-test-scope.md).
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class ActiveSessionTableTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "ActiveSessionTable");

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
        if (!proc.WaitForExit(120_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("al-runner did not exit within 120s.");
        }
        // WaitForExit(int) returns as soon as the process exits and does NOT wait for the
        // async BeginOutputReadLine/BeginErrorReadLine callbacks to drain — only the
        // parameterless overload does. Without this the last stdout lines can still be in
        // flight when we read outSb, and an Assert.Contains on a line the runner definitely
        // printed fails intermittently, the more so the more loaded the machine is (#2496).
        proc.WaitForExit();
        return (proc.ExitCode, outSb.ToString(), errSb.ToString());
    }

    [Fact]
    public void ActiveSession_ReadingSessionRow_ColdAndWarm()
    {
        var cacheDir = TestScratch.Dir("al-runner-ast-tests");
        try
        {
            foreach (var pass in new[] { "cold", "warm" })
            {
                var (exit, stdout, stderr) = Run(cacheDir);
                Assert.True(exit == 0,
                    $"{pass} run: every fixture test must pass. exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
                // The row exists under (ServiceInstanceId(), SessionId()) — the direct RED.
                Assert.Contains("PASS  Codeunit70581.ActiveSession_GetByInstanceAndSessionId_FindsARow", stdout);
                // Read back, not invented: a row of defaults has a blank User ID and a null SID.
                Assert.Contains("PASS  Codeunit70581.ActiveSession_Row_UserIdAndSidAreTheSessionUser", stdout);
                // One login instant across Session and Active Session.
                Assert.Contains("PASS  Codeunit70581.ActiveSession_Row_LoginDatetimeIsTheSessionTablesLoginInstant", stdout);
                Assert.Contains("PASS  Codeunit70581.ActiveSession_Row_CarriesASessionUniqueId", stdout);
                // Client Type is BC's own mapping of the (unset) skeleton connection type: a constant.
                Assert.Contains("PASS  Codeunit70581.ActiveSession_Row_ClientTypeIsBcsMappingOfTheSkeletonConnectionType", stdout);
                // Negative; passes against an empty table too, so not sufficient alone.
                Assert.Contains("PASS  Codeunit70581.ActiveSession_GetOnASessionIdThatIsNoSession_ReturnsFalse", stdout);
                Assert.DoesNotContain("FAIL", stdout);
                Assert.DoesNotContain("ActiveSessionSystemTable: the session's Active Session row", stderr);
            }
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
