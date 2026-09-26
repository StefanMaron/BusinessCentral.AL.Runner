// SingleInstanceSessionLifetimeTests — issue #4781.
//
// Runner-mechanism test over Fixtures/SingleInstanceSessionLifetime. What it pins is the
// runner's own bookkeeping across a test-codeunit boundary, which only exists because the
// runner replaces its table store there:
//   - the SingleInstance instance cache is no longer dropped at the boundary;
//   - every record that instance reaches is re-pointed at the replacement store on its next
//     read (RecordPatches.RecordImplementation_LiveDataAccess, wired by Cecil into every read of
//     RecordImplementation.dataAccess), keeping its filters: held directly, in an array, as a
//     RecordRef, three codeunits down, through an interface, a variant, a List or a Dictionary,
//     and on the Get and BLOB CalcFields paths the runner reads by reflection;
//   - the per-boundary sweep of the shared-object container keeps a List/Dictionary the
//     instance holds (BcRuntime.TreeObjectsReachableFromSingleInstances);
//   - the reset after the install seed still keeps install-trigger state from the first test.
// The bundle-start reset needs two runs in one process: SingleInstanceServerResetTests.
//
// The BC claim underneath (a SingleInstance instance lives on the company scope and survives a
// TestIsolation = Codeunit boundary) was measured on the Windows reference container and an MS
// SaaS sandbox with corpus codeunit 60600's former assertion — expected 0, got 99 — recorded in
// corpus issue #213. A corpus test cannot cross a test-codeunit boundary without depending on
// run order, which the corpus refuses by construction (corpus #261,
// check-singleinstance-fixture-owners.py). This fixture can, because the runner fixes the order
// by object id (#2801).
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class SingleInstanceSessionLifetimeTests
{
    private const int SpawnTimeoutMs = 120_000;

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "SingleInstanceSessionLifetime");

    private static (int ExitCode, string StdOut, string StdErr) Run(string cacheDir)
    {
        var sb = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
        sb.Append(' ').Append($"\"{FixtureDir}\"");
        sb.Append(' ').Append($"--cache \"{cacheDir}\"");
        sb.Append(" --show-pass");

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
        proc.WaitForExit();   // drain the async readers (#2496)
        return (proc.ExitCode, outSb.ToString(), errSb.ToString());
    }

    [Fact]
    public void SingleInstanceState_SurvivesTheTestCodeunitBoundary_AndItsRecordsReadTheLiveStore()
    {
        var cacheDir = TestScratch.Dir("al-runner-si-lifetime");
        try
        {
            var (exit, stdout, stderr) = Run(cacheDir);

            Assert.True(exit == 0,
                $"expected every fixture test to pass. exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
            // Install-trigger state is reset before the first test, and nothing else is there.
            Assert.Contains("PASS  Codeunit71930.FirstCodeunit_StartsClean_ThenLeavesState", stdout);
            // The boundary keeps the instance.
            Assert.Contains("PASS  Codeunit71931.SecondCodeunit_SeesTheFirstCodeunitsSingleInstanceState", stdout);
            // ...and re-points its records at the rolled-back store without resetting them.
            Assert.Contains("PASS  Codeunit71931.SecondCodeunit_SingleInstanceRecordsReadTheRolledBackStore", stdout);
            Assert.Contains("Tests: 3   passed 3", stdout);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
