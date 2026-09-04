// FeatureKeyVirtualTableTests — issue #2585.
//
// A RUNNER-MECHANISM test: it proves the ROUTE works — that table 2000000211 reaches BC's own
// FeatureKeyDataProvider and its rows land where AL can read them — not which features BC
// ships. Naming a specific key here would pin a BC version; which keys exist is what the
// corpus adjudicates across all eight legs.
//
// Before the fix, 2000000211 had no provider, so GetDataAccessForTableCore fell through to the
// plain in-memory temp store and every read answered zero rows. Base Application's Feature
// Management reads this table to choose between a feature's modern and legacy implementation,
// so an empty table made every feature read as unregistered and the legacy path win silently.
//
// The rows are BC's own. FeatureKey.BuildFeatureKeys() is a hardcoded static list in
// Microsoft.Dynamics.Nav.Types and the runner already loads that DLL, so rebuilding the list
// here would be a second copy that drifts — and inserting one hardcoded row to steer a single
// feature, which is what prompted the issue, is the silent fake loud-failures.md bars.
//
// MEASURED on BC 28.1.49838.53910: 14 rows, every one State = None. Recorded because the
// issue predicted CalcOnlyVisibleFlowFields would be present and AllUsers, and it is not —
// that string does not appear in Types.dll or Ncl.dll on 28.1 OR 28.4. Hence no assertion
// here names a key or a state.
//
// The last test is the one that must not regress: real BC's Modify rejects a change to a
// read-only column BY NAME (issue #2636), before any write-through happens.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class FeatureKeyVirtualTableTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "FeatureKeyVirtualTable");

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
    public void FeatureKey_RoutedToBcsOwnProvider_AllFixtureTestsPass()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "al-runner-fkv-tests", "cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (exit, stdout, stderr) = Run(cacheDir);

            Assert.True(exit == 0,
                $"expected a clean run (every fixture test must pass). exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");

            // The route works at all — the direct RED this fixes.
            Assert.Contains("PASS  Codeunit60821.FeatureKey_AnswersBcsOwnRows", stdout);
            // Rules out N blank rows, and proves Get reaches the same rowset FindSet walked.
            Assert.Contains("PASS  Codeunit60821.FeatureKey_EveryRowHasANonBlankIdThatGetRoundTrips", stdout);
            // Negative: a provider answering every Get with a row would pass the above.
            Assert.Contains("PASS  Codeunit60821.FeatureKey_GetOnAnUnknownId_ReturnsFalse", stdout);
            // The read-only contract: changing a read-only column raises BC's own error naming
            // that column, before any write-through happens (#2636).
            Assert.Contains("PASS  Codeunit60821.FeatureKey_Modify_ChangingAReadOnlyColumn_RaisesNamingTheField", stdout);
            Assert.DoesNotContain("FAIL", stdout);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
