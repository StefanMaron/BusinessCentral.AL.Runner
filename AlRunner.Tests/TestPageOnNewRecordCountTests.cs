// TestPageOnNewRecordCountTests — issues #3029 and #3481.
//
// A RUNNER-MECHANISM test over the fixture in Fixtures/TestPageOnNewRecordCount, whose AL arms
// mirror corpus codeunit 60358 "ONRC Tests" (StefanMaron/BusinessCentral.AL.Language.Tests),
// which measures the same deltas on real BC. Every count is a DELTA from a baseline the arm
// measures itself: the absolute cost of opening over an empty part is tier-dependent and the
// runner does not reproduce it (#3481). The history of the five-firing defect this pinned
// first is in PR #3414's body; the New() delta is #3029's last arm.
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageOnNewRecordCountTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "TestPageOnNewRecordCount");

    [Fact]
    public async Task OnNewRecordFirings_MatchTheDeltasRealBcMeasures()
    {
        var r = await SuiteServer.RunViaServer(FixtureDir);

        Assert.True(r.ExitCode == 0,
            $"every fixture test must pass. exit={r.ExitCode}\n{r}");

        // #3029: New() on a part already parked on its started draft line is a NEW
        // new-record step (+1), not a commit of the draft line's row (+0).
        r.AssertPassed("Codeunit70646.New_OnEmptyLinkedPart_RaisesOnNewRecordOnceMoreThanTheOpen");

        // First() on an already-open empty part: +0.
        r.AssertPassed("Codeunit70646.DraftLine_ShownAndUntouched_FirstAddsNothingToTheOpen");

        // A write into a started draft line: +0 — the promotion must not re-run the step.
        r.AssertPassed("Codeunit70646.DraftLine_ShownThenWritten_WriteAddsNothing");

        // A part WITH rows: 0 on the data row, +1 onto the draft line, +0 for the write.
        r.AssertPassed("Codeunit70646.DraftLine_ReachedByNextThenWritten_RaisesOnNewRecordOnce");
        r.AssertPassed("Codeunit70646.ExistingDataRows_WalkedAcross_RaiseOnNewRecordNotAtAll");

        // The negative direction: a second row costs +1, so the +0s above are not a latch
        // that never resets.
        r.AssertPassed("Codeunit70646.TwoRowsThroughTheDraftLine_SecondRowRaisesOnNewRecordOnceMore");
        r.AssertPassed("Codeunit70646.DraftLineAbandonedByAParentMove_MakesTheNextRowOweItsOwnFiring");

        r.AssertNoFailures();
    }
}
