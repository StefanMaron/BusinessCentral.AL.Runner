// TestPageOnNewRecordCountTests — issues #3029 and #3481.
//
// A RUNNER-MECHANISM test over the fixture in Fixtures/TestPageOnNewRecordCount, whose AL arms
// mirror corpus codeunit 60358 "ONRC Tests" (StefanMaron/BusinessCentral.AL.Language.Tests),
// which measures the same deltas on real BC. Every count is a DELTA from a baseline the arm
// measures itself: the absolute cost of opening over an empty part is tier-dependent and the
// runner does not reproduce it (#3481). The history of the five-firing defect this pinned
// first is in PR #3414's body; the New() delta is #3029's last arm.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageOnNewRecordCountTests
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
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "TestPageOnNewRecordCount");

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
    public void OnNewRecordFirings_MatchTheDeltasRealBcMeasures()
    {
        var cacheDir = TestScratch.Dir("al-runner-onc-tests");
        try
        {
            var (exit, stdout, stderr) = Run(cacheDir);

            Assert.True(exit == 0,
                $"every fixture test must pass. exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");

            // #3029: New() on a part already parked on its started draft line is a NEW
            // new-record step (+1), not a commit of the draft line's row (+0).
            Assert.Contains("PASS  Codeunit70646.New_OnEmptyLinkedPart_RaisesOnNewRecordOnceMoreThanTheOpen", stdout);

            // First() on an already-open empty part: +0.
            Assert.Contains("PASS  Codeunit70646.DraftLine_ShownAndUntouched_FirstAddsNothingToTheOpen", stdout);

            // A write into a started draft line: +0 — the promotion must not re-run the step.
            Assert.Contains("PASS  Codeunit70646.DraftLine_ShownThenWritten_WriteAddsNothing", stdout);

            // A part WITH rows: 0 on the data row, +1 onto the draft line, +0 for the write.
            Assert.Contains("PASS  Codeunit70646.DraftLine_ReachedByNextThenWritten_RaisesOnNewRecordOnce", stdout);
            Assert.Contains("PASS  Codeunit70646.ExistingDataRows_WalkedAcross_RaiseOnNewRecordNotAtAll", stdout);

            // The negative direction: a second row costs +1, so the +0s above are not a latch
            // that never resets.
            Assert.Contains("PASS  Codeunit70646.TwoRowsThroughTheDraftLine_SecondRowRaisesOnNewRecordOnceMore", stdout);
            Assert.Contains("PASS  Codeunit70646.DraftLineAbandonedByAParentMove_MakesTheNextRowOweItsOwnFiring", stdout);

            Assert.DoesNotContain("FAIL", stdout);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
