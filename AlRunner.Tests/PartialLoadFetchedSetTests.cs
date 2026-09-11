// PartialLoadFetchedSetTests — the runner-side mechanism behind #3859.
//
// WHAT THE FIX IS. Real BC's AreFieldsLoaded reads the MATERIALISED BUFFER
// (RecordImplementation.AreFieldsLoaded -> mutableRecordBuffer.ReadOnlyBuffer.IsFieldUnloaded);
// the runner used to read the REQUESTED set (TableState.FieldLoadInfo) instead, and that is
// wrong in whichever direction the request was last moved. The runner cannot simply switch to
// reading the buffer, because only SqlTableDataProvider and its helpers ever construct a
// ReadOnlyRecordBuffer carrying a real FieldLoadInfo — measured with find_callers over all four
// ReadOnlyRecordBuffer constructors on 27.5 — and every table here goes through
// TempTableDataProvider, whose buffers carry the table's DEFAULT load info. So the runner keeps
// its own record of what a fetch materialised, which is what these tests pin.
//
// WHY THESE LIVE HERE AND NOT UPSTREAM. The BEHAVIOUR is pinned upstream and adjudicated on
// real service tiers: corpus codeunit 60766 (three arms, the buffer answer) and 60775 (six
// tests, including the ones that forced the requested-set answer in #3358), both green on all
// eight cloud legs. Nothing below re-asserts a claim about BC. What is measured here is the
// runner's own wiring — which Cecil registrations exist and what each one is for — which no AL
// statement can observe and no service tier can adjudicate (bc-behavior-tests-go-upstream.md).
//
// THE TRAP A LATER EDITOR HITS. The three registrations are a SET, and dropping any one of them
// leaves the other two answering confidently and wrongly:
//   * AreFieldsLoaded alone, without the ClearRecord reset, reports a field that Clear(Rec)
//     discarded as still loaded (corpus 60766 arm 3's negative half went red on exactly this).
//   * AreFieldsLoaded alone, without the AddLoadField hook, reports a field that a JIT read just
//     materialised as unloaded (corpus 60775 PartialLoad_ReadOmittedField_JitLoadsRealValue went
//     red on exactly this).
// Both were observed during the fix, not predicted; each test below names the arm it protects.
using System;
using System.IO;
using Xunit;

namespace AlRunner.Tests;

public sealed class PartialLoadFetchedSetTests
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

    private static string Patch() =>
        Read("AlRunner", "Patches", "RecordPatches.PartialLoad.cs");

    // ══ 1. The reset ═════════════════════════════════════════════════════════════════════
    //
    // ClearRecord's whole body is `mutableRecordBuffer = null; ResetRecord();` — the row is
    // discarded, so the record of what a fetch materialised must be discarded with it. It is a
    // PREPEND, not a replacement, because BC's own clear still has to happen; assert that too,
    // since a ReplaceBodyWithHelper here would silently stop Clear(Rec) from clearing anything.
    [Fact]
    public void ClearRecordReset_IsRegistered_AsAPrependSoBcsOwnClearStillRuns()
    {
        var src = Rewrites();

        Assert.Contains("RecordImplementation_ClearRecord_Prologue", src, StringComparison.Ordinal);
        Assert.Contains("\"RecordImplementation\", \"ClearRecord\", 0", src, StringComparison.Ordinal);

        // The registration must be a prepend. Locate the helper's own call and read the
        // construct it sits in, rather than asserting PrependStaticCall appears anywhere in an
        // 8,000-line file — which it does, for unrelated rewrites.
        var at = src.IndexOf("RecordImplementation_ClearRecord_Prologue", StringComparison.Ordinal);
        Assert.True(at > 0, "the ClearRecord helper is not referenced, so nothing was measured");
        var window = src.Substring(Math.Max(0, at - 400), Math.Min(500, src.Length - Math.Max(0, at - 400)));
        Assert.Contains("PrependStaticCall", window, StringComparison.Ordinal);
        Assert.DoesNotContain("ReplaceBodyWithHelper", window, StringComparison.Ordinal);
    }

    // ══ 2. The JIT load ══════════════════════════════════════════════════════════════════
    //
    // GetFieldValue calls AddLoadField when AL reads a field the load set omits, and after that
    // call the value is genuinely in hand — so the field becomes materialised exactly as a fetch
    // materialises one. Same prepend requirement: BC's own
    // TrySetNewFieldLoadInfoAndInvalidate must still run.
    [Fact]
    public void JitLoadHook_IsRegistered_AsAPrependOnAddLoadField()
    {
        var src = Rewrites();

        Assert.Contains("RecordImplementation_AddLoadField_Prologue", src, StringComparison.Ordinal);
        Assert.Contains("\"RecordImplementation\", \"AddLoadField\", \"NCLMetaField\"",
            src, StringComparison.Ordinal);

        var at = src.IndexOf("RecordImplementation_AddLoadField_Prologue", StringComparison.Ordinal);
        Assert.True(at > 0, "the AddLoadField helper is not referenced, so nothing was measured");
        var window = src.Substring(Math.Max(0, at - 400), Math.Min(500, src.Length - Math.Max(0, at - 400)));
        Assert.Contains("PrependStaticCall", window, StringComparison.Ordinal);
        Assert.DoesNotContain("ReplaceBodyWithHelper", window, StringComparison.Ordinal);
    }

    // ══ 3. The answer has no requested-set fallback ══════════════════════════════════════
    //
    // This is the whole fix in one property. Once a fetch has happened the fetched set is
    // AUTHORITATIVE: a field it does not hold is unloaded, full stop. Reinstating a fallback to
    // IsFieldSelectedForLoad — the natural-looking "be lenient" edit — answers from the
    // REQUESTED set again and re-breaks corpus 60766 arm 2, which asserts that widening the
    // request without re-fetching does NOT make an absent field loaded.
    //
    // IsFieldSelectedForLoad is still used, by the fold that records a fetch; what must not
    // come back is a consultation of it in the ANSWERING loop.
    [Fact]
    public void TheAnswer_ReadsTheFetchedSetOnly_WithNoRequestedSetFallback()
    {
        var src = Patch();

        // The fetched set is what the answer reads.
        Assert.Contains("state.Loaded.Contains", src, StringComparison.Ordinal);

        // And IsFieldSelectedForLoad is consulted exactly once — in the fold, never in the
        // answer. Two occurrences of the invoke would mean the fallback is back.
        var invokes = CountOccurrences(src, "_riIsFieldSelectedForLoad!.Invoke");
        Assert.True(invokes == 1,
            $"IsFieldSelectedForLoad is invoked {invokes} time(s); exactly 1 is expected — it "
            + "belongs in the fetch fold only. A second invoke is the requested-set fallback "
            + "that corpus 60766 arm 2 (widen-without-refetch) measures as wrong.");
    }

    // ══ 4. A fetch is identified by BUFFER IDENTITY, not by value ════════════════════════
    //
    // A re-fetch installs a NEW MutableRecordBuffer, which is what makes "a fetch happened"
    // observable at all without a hook on every fetch path. Comparing by value instead would
    // make a re-fetch of the same row invisible, and corpus 60766 arm 3 — narrow, then re-fetch
    // — is the arm that measures the difference.
    [Fact]
    public void TheFetchEvent_IsBufferIdentity_NotAValueComparison()
    {
        var src = Patch();

        Assert.Contains("ReferenceEquals(state.LastFoldedBuffer, buffer)", src, StringComparison.Ordinal);
    }

    // ══ 5. The audit justification is present ════════════════════════════════════════════
    //
    // loud-failures.md requires every patch under AlRunner/Patches/ to state why its answer is
    // observably equivalent to real BC's, with a citation a reader can follow. The citation here
    // is the pair of corpus codeunits that adjudicated both directions on real tiers.
    [Fact]
    public void ThePatch_CitesTheCorpusCodeunitsThatAdjudicatedBothDirections()
    {
        var src = Patch();

        Assert.Contains("60766", src, StringComparison.Ordinal);
        Assert.Contains("60775", src, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var n = 0;
        var i = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (i >= 0)
        {
            n++;
            i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal);
        }
        return n;
    }
}
