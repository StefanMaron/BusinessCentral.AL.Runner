// DateVirtualTableLazyWindowTests — issue #2648.
//
// The Date system virtual table (2000000007) is computed per request on a real service tier and
// spans years 1 through 9999. The runner serves it from an in-memory store instead, so it has to
// INSERT rows, and before this fix it inserted the whole default window — 1900-01-01 to
// 2099-12-31, about 87,000 rows across the five period types — on the first touch of
// `Record Date`, whatever the request had asked for. A filter naming one week in 1850 cost about
// 109,000 row inserts to return 7 rows.
//
// WHY THE CAP IS THE INSTRUMENT, AND NOT A STOPWATCH
//   "It got faster" is not a testable claim: wall clock on a shared machine has measured 1.9 s
//   and 3.1 s for identical work. The runner already has an exact, deterministic counter for the
//   thing that actually changed — AL_RUNNER_DATE_WINDOW_MAX_ROWS, the cap PopulateDateSpan checks
//   an estimated row count against before materialising anything. Setting it to 2,000 is a
//   statement about ROWS: below the ~87,000 the default window needs, above the 25 a one-week
//   span needs. So the bounded read in the fixture can only answer if the window was never
//   materialised, and the unfiltered read can only refuse if it still is for a request that
//   needs it.
//
//   Run against the pre-fix runner the fixture's first two tests fail with
//   RunnerOutOfScopeException naming the cap, because the eager population refused before any
//   filter was consulted. That is the RED.
//
// This is a RUNNER-MECHANISM claim, not a claim about what BC does. What the Date table itself
// answers — weekday numbering, ISO weeks, month ends, "Period End" being a closing date — is
// plain BC behaviour and is covered upstream in the al-language corpus (codeunit 60983, "Test
// Date Virtual Table"), per .claude/rules/bc-behavior-tests-go-upstream.md.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class DateVirtualTableLazyWindowTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "DateWindowLazy");

    /// <summary>
    /// Well below the ~87,000 rows the default 1900..2099 window holds, and well above the 25
    /// rows EstimateDateRowCount returns for a single week. Any value strictly between those two
    /// works; 2,000 is far enough from both that a small change to either does not silently turn
    /// this test into a tautology.
    /// </summary>
    private const string RowCap = "2000";

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
        psi.Environment["AL_RUNNER_DATE_WINDOW_MAX_ROWS"] = RowCap;

        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        using var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (outSb) outSb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errSb) errSb.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(180_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("al-runner did not exit within 180s.");
        }
        // WaitForExit(int) does not drain the async output callbacks; only the parameterless
        // overload does. Without this the last stdout lines can still be in flight.
        proc.WaitForExit();
        return (proc.ExitCode, outSb.ToString(), errSb.ToString());
    }

    [Fact]
    public void DateWindow_IsMaterialisedPerRequest_NotWholesaleOnFirstTouch()
    {
        var cacheDir = Path.Combine(
            Path.GetTempPath(), "al-runner-date-lazy-tests", "cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (exit, stdout, stderr) = Run(cacheDir);

            Assert.True(exit == 0,
                $"expected a clean run (all three fixture tests must pass). exit={exit}\n"
                + $"stdout:\n{stdout}\nstderr:\n{stderr}");

            // RED before the fix: eager population refused under the 2,000-row cap before the
            // filter was ever read, so this failed with an out-of-scope error naming the cap.
            Assert.Contains(
                "PASS  Codeunit61631.Date_BoundedFilter_MaterialisesOnlyTheBoundedSpan", stdout);
            // The keyed-Get route (#2870) has to be lazy too — it does not reach the find path.
            Assert.Contains(
                "PASS  Codeunit61631.Date_KeyedGetInABoundedSpan_MaterialisesOnlyThatPeriod", stdout);
            // The control, and the half that must NOT have changed: a read naming no closed
            // "Period Start" bound is still answered from the whole documented window, so under
            // this cap it must still refuse by name rather than answer from fewer rows.
            Assert.Contains(
                "PASS  Codeunit61631.Date_UnfilteredRead_StillDemandsTheWholeDocumentedWindow", stdout);
            Assert.DoesNotContain("FAIL", stdout);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
