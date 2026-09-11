// PartialLoadDeadRewriteTests — why three partial-load Cecil rewrites produced zero red tests,
// and what pins them now (#3372 item 3).
//
// ── THE OBSERVATION THE ISSUE RECORDED ───────────────────────────────────────────────────
// The reviewer of #3365 no-opped three rewrites — NavRecord.ALSetBaseLoadFields/0 and /1, and
// RecordImplementation.SetLoadFields(FieldLoadInfo) — and got ZERO red tests, while a fourth
// break on the same type did turn a test red. The issue's stated hypothesis was R2R inlining
// of the thin AL* forwarders, the documented trap where a hook on a tiny method is bypassed.
//
// ── WHAT IT ACTUALLY IS, MEASURED ────────────────────────────────────────────────────────
// Not inlining. Nothing on any AL-reachable path CALLS those three. Measured with
// find_callers over Microsoft.Dynamics.Nav.Ncl.dll, on 27.0 and 28.4, identical on both:
//
//   ALSetBaseLoadFields()            ZERO callers anywhere in Ncl.dll
//   ALSetBaseLoadFields(DataError)   one caller: ALSetBaseLoadFields/0, itself uncalled
//   RecordImplementation
//     .SetLoadFields(FieldLoadInfo)  one caller: DataItemIterator.SetLoadFieldsBasedOnMetadata
//
// The third one is the interesting case, and it is self-inflicted rather than dead upstream:
// the runner ALREADY Cecil-no-ops DataItemIterator.SetLoadFieldsBasedOnMetadata — see
// NclCecilRewrite.Runtime.cs, "partial records disabled; full field load" — so its body is a
// bare `ret` before it can reach the only call site of the FieldLoadInfo overload.
//
// AL's own SetLoadFields does not go near any of the three. It routes
// NavRecord.SetLoadFields(DataError, int[]) -> recordImplementation.SetLoadFields(HashSet),
// which is the ISet OVERLOAD — a different method from the FieldLoadInfo one that was no-opped.
//
// So "no test isolates these three" is not a coverage gap to be filled with a contrived test,
// and it is not the "verified only in aggregate" the issue offered as the fallback. Two of the
// three are unreachable in BC itself, and the third is unreachable because of a deliberate
// runner rewrite. A test that turned red when they were no-opped would mean something had
// started calling them — which is exactly what these tests watch for.
//
// ── WHY THIS IS THE PROPORTIONATE FIX ────────────────────────────────────────────────────
// The issue asked for one of two things: find an outer caller to rewrite instead, or write
// down that the surface is verified only in aggregate. Neither applies once the call graph is
// measured. What is worth pinning is the REASON, so that a later BC version wiring a caller up
// — or a later runner change dropping the DataItemIterator no-op — does not silently leave
// three rewrites acting on a live path with nothing measuring them.
//
// ── WHY THESE LIVE IN AlRunner.Tests AND NOT THE UPSTREAM CORPUS ─────────────────────────
// The subject is which Cecil rewrites the runner registers and why, plus the runner's own
// no-op of DataItemIterator.SetLoadFieldsBasedOnMetadata. No AL statement can observe any of
// it and a service tier has nothing to adjudicate, so bc-behavior-tests-go-upstream.md places
// it here. The partial-record BEHAVIOUR these rewrites sit next to is pinned upstream, by
// corpus codeunit 60775.
using System;
using System.IO;
using Xunit;

namespace AlRunner.Tests;

public sealed class PartialLoadDeadRewriteTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    // A source read that silently returned "" would make every Contains assertion below pass
    // vacuously — the absent thing reading as the success answer. So it refuses instead
    // (guards-need-a-third-state.md).
    private static string Read(params string[] parts)
    {
        var path = Path.Combine(RepoRoot, Path.Combine(parts));
        Assert.True(File.Exists(path), $"source not found, so nothing was measured: {path}");
        var src = File.ReadAllText(path);
        Assert.False(string.IsNullOrWhiteSpace(src), $"source is empty, so nothing was measured: {path}");
        return src;
    }

    private static string Rewrites() =>
        Read("AlRunner", "Infrastructure", "NclCecilRewrite.Runtime.cs");

    // ══ 1. The no-op that makes the third rewrite unreachable is still there ══════════════
    //
    // This is the one of the three that is NOT dead in BC. Its only caller,
    // DataItemIterator.SetLoadFieldsBasedOnMetadata, is reachable on a real tier and is
    // unreachable here only because the runner empties it. Drop that no-op and the
    // FieldLoadInfo overload goes live on the report data-item path with nothing measuring it,
    // which is precisely the state this test exists to make loud.

    // Assert the QUOTED method name as the Cecil lookup spells it, not a bare substring: a
    // bare Contains("SetLoadFieldsBasedOnMetadata") also matches "...MetadataXX", so renaming
    // the lookup target away left this test green. Measured — that mutation passed 6/6 before
    // this line was tightened, which is the "test green in both states" defect one level up.
    [Fact]
    public void DataItemIteratorNoOp_IsStillRegistered()
    {
        var src = Rewrites();

        Assert.Contains("\"DataItemIterator\", \"SetLoadFieldsBasedOnMetadata\", 1",
            src, StringComparison.Ordinal);
        Assert.Contains("partial records disabled", src, StringComparison.Ordinal);
    }

    // The reason the no-op is safe is BC's own JIT-load contract, and it is stated at the
    // registration rather than left to be rediscovered. If the justification goes, the claim
    // that skipping the optimization is faithful has nothing behind it (loud-failures.md's
    // audit obligation).
    [Fact]
    public void DataItemIteratorNoOp_StatesWhySkippingItIsFaithful()
    {
        var src = Rewrites();

        Assert.Contains("superset", src, StringComparison.Ordinal);
    }

    // ══ 2. The AreFieldsLoaded rewrite — the one that IS on a live AL path ════════════════
    //
    // The contrast that makes item 3's finding meaningful: of the four rewrites the #3365
    // review probed, this is the one a corpus test can turn red, because AL reaches it.

    [Fact]
    public void AreFieldsLoadedRewrite_IsStillRegistered_AndNamesItsHelper()
    {
        var src = Rewrites();

        Assert.Contains("\"AreFieldsLoaded\"", src, StringComparison.Ordinal);
        Assert.Contains("RecordImplementation_AreFieldsLoaded", src, StringComparison.Ordinal);
    }

    // ══ 3. The finding itself is written down where the next reader will be ══════════════
    //
    // Without this, the next person to no-op those three and see zero red tests repeats the
    // whole investigation, and the R2R-inlining hypothesis is the one they will reach for —
    // it is plausible, it is documented elsewhere in this repo, and it is wrong here.

    [Fact]
    public void TheDeadCallGraphFinding_IsRecordedForTheNextReader()
    {
        var src = Read("AlRunner", "Patches", "RecordPatches.PartialLoad.cs");

        Assert.Contains("ALSetBaseLoadFields", src, StringComparison.Ordinal);
        Assert.Contains("#3372", src, StringComparison.Ordinal);
    }

    // The claim must name what settled it, not merely assert it: a reader who cannot re-run the
    // measurement cannot check the account, and loud-failures.md's citation clause (#3399) is
    // about exactly that — a correct finding reached by a misdescribed method is
    // indistinguishable from a wrong one.
    [Fact]
    public void TheFinding_CitesHowItWasMeasured_NotJustItsConclusion()
    {
        var src = Read("AlRunner", "Patches", "RecordPatches.PartialLoad.cs");

        Assert.Contains("find_callers", src, StringComparison.Ordinal);
        // Both BC versions the measurement was taken on, so "identical on both" is checkable.
        Assert.Contains("27.0", src, StringComparison.Ordinal);
        Assert.Contains("28.4", src, StringComparison.Ordinal);
    }

    // The hypothesis that turned out to be wrong is named as wrong. An investigation that
    // records only its conclusion invites the next reader to re-derive the discarded branch.
    [Fact]
    public void TheFinding_SaysItIsNotR2rInlining()
    {
        var src = Read("AlRunner", "Patches", "RecordPatches.PartialLoad.cs");

        Assert.Contains("inlin", src, StringComparison.OrdinalIgnoreCase);
    }
}
