// TestPageOnNewRecordCountTests — issue #3029.
//
// A RUNNER-MECHANISM test. The BC-behaviour claim it rests on is measured upstream against a
// live service tier: corpus codeunit 60358 "ONRC Tests"
// (StefanMaron/BusinessCentral.AL.Language.Tests#266) asks the same five questions of real BC.
// This suite exists so a regression in the runner's own draft-line wiring fails loudly HERE,
// without the submodule pin having to be bumped first.
//
// WHAT WAS WRONG. A part page's OnNewRecord ran FIVE times for one draft-line row. Measured on
// the fixture below before the fix, by stack-tracing every call that reached the platform's
// new-record step:
//
//   1. EagerlyBuildParts -> GetPart -> ReloadLinkedRow          (host still opening, parent
//                                                                position reads `Field1=0()` —
//                                                                the part was parked on a draft
//                                                                line for a parent row that did
//                                                                not exist yet)
//   2. MoveFirstDuringOpen -> Loaded -> RefreshLinkedParts -> ReloadLinkedRow
//   3. ALGoToRecord -> FindRowFromFieldValues -> RefreshLinkedParts -> ReloadLinkedRow
//   4. the test's own Lines.First() -> LiveNavTestPart.MoveFirst -> MoveFirst
//   5. the write -> PromoteNewRowLineForWrite -> InsertEmptyRow -> TryNewRecord
//
// Only #5 is the one issue #3029 reported, and it is real — measured delta of exactly +1 across
// the SetValue. The other four were not in the issue at all: they are page plumbing re-entering
// a draft line the page was already parked on, and they cost four firings for a row the test
// had not asked for yet. So the reported line was one instance of the shape, and the shape was
// five.
//
// WHY NO EXISTING TEST CAUGHT ANY OF IT. Every fixture in this area uses an ASSIGNMENT as its
// OnNewRecord witness — corpus codeunit 60996 writes `Rec."Set By OnNewRecord" := 'NEWREC'` —
// and an assignment is idempotent. One firing and five firings leave the same value. The
// fixture here appends a row to a log table instead, so the witness is a count and each
// assertion names a concrete integer.
//
// WHY IT MATTERS. An OnNewRecord that only assigns defaults cannot tell. One with a side effect
// — a number-series draw, a log row, a counter, a call into a setup codeunit — is silently
// multiplied by five, and the finished row does not say so.
//
// WHAT THIS SUITE DOES AND DOES NOT CLAIM ABOUT BC. The claim it makes is that landing on an
// EXISTING DATA ROW costs zero firings, that stepping onto the draft line costs +1, that writing
// into an already-started draft line costs +0, and that a second row costs +1 more. Two real
// service tiers agree on every one of those: bc-linux on all 8 cloud legs (corpus run
// 34140530877) and the official Microsoft Windows container (nightly run 34182689878).
//
// What it deliberately does NOT claim is the absolute cost of opening a card over an EMPTY part,
// where the two tiers answer 6 and 3 respectively and which of them is right is under
// investigation. The runner answers 1 there. That number appears in the arms below only as the
// baseline they happen to compare against, and it is not asserted as BC behaviour anywhere —
// which is why ExistingDataRows_WalkedAcross_RaiseOnNewRecordNotAtAll exists: it is the arm that
// states the portable claim as a delta, so it stays true whatever the opening cost settles at.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageOnNewRecordCountTests
{
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
        if (!proc.WaitForExit(180_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("al-runner did not exit within 180s.");
        }
        // WaitForExit(int) does not drain the async output callbacks; the parameterless
        // overload does. See #2496.
        proc.WaitForExit();
        return (proc.ExitCode, outSb.ToString(), errSb.ToString());
    }

    [Fact]
    public void OneDraftLineRow_RaisesThePartPagesOnNewRecordExactlyOnce()
    {
        var cacheDir = TestScratch.Dir("al-runner-onc-tests");
        try
        {
            var (exit, stdout, stderr) = Run(cacheDir);

            Assert.True(exit == 0,
                $"every fixture test must pass. exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");

            // The control. New() is one new-record step by construction, so this failing would
            // mean the fixture cannot count and no other assertion here could be read.
            Assert.Contains("PASS  Codeunit70646.New_OnEmptyLinkedPart_RaisesOnNewRecordOnce", stdout);

            // Merely showing the draft line — the four plumbing re-entries. Was 4.
            Assert.Contains("PASS  Codeunit70646.DraftLine_ShownAndUntouched_RaisesOnNewRecordOnce", stdout);

            // The defect #3029 reported: the write's own extra firing. Was 5.
            Assert.Contains("PASS  Codeunit70646.DraftLine_ShownThenWritten_RaisesOnNewRecordOnce", stdout);

            // The other route onto the draft line, walking off the end of real data. This arm
            // also asserts 0 firings while standing on an existing row, which is what stops the
            // rest from being read as "any cursor move fires the trigger".
            Assert.Contains("PASS  Codeunit70646.DraftLine_ReachedByNextThenWritten_RaisesOnNewRecordOnce", stdout);

            // THE ARM THAT DOES NOT REST ON THE RUNNER'S OWN OPENING COST, and the only one
            // whose numbers both service tiers agree with. It asserts the existing-row walk as
            // a DELTA of zero and the step onto the draft line as +1, so it holds whatever the
            // empty-part opening cost turns out to be. Was: opening a card over a part that
            // already HAS rows raised the trigger once, where both tiers raise it not at all.
            Assert.Contains("PASS  Codeunit70646.ExistingDataRows_WalkedAcross_RaiseOnNewRecordNotAtAll", stdout);

            // THE NEGATIVE DIRECTION, and the one that makes the fix a de-duplication rather
            // than a suppression: two rows must still cost two firings. A latch that never
            // reset would pass every assertion above and fail this one.
            Assert.Contains("PASS  Codeunit70646.TwoRowsThroughTheDraftLine_RaiseOnNewRecordTwice", stdout);

            Assert.DoesNotContain("FAIL", stdout);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
