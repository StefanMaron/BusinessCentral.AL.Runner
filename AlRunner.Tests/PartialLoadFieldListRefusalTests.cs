// PartialLoadFieldListRefusalTests — the `fields is null` arm of the AreFieldsLoaded patch
// refuses instead of answering the success value (#3372 item 1).
//
// ── WHAT WAS THERE, AND WHY IT WAS THE ONE WRITTEN THE OTHER WAY ─────────────────────────
// RecordPatches.PartialLoad.cs replaces RecordImplementation.AreFieldsLoaded(IEnumerable<
// NCLMetaField>). Its `fields is null` arm returned TRUE — the success answer — for an input
// it had not interpreted. loud-failures.md forbids exactly that: a surface the runner cannot
// answer faithfully throws, naming the API and the reason, never returns a default.
//
// The reviewer of #3365 judged the arm unreachable from AL, and that reading is correct TODAY:
// NavRecord.AreFieldsLoaded(DataError, params int[]) calls ArgumentNullException.ThrowIfNull
// (fields) and then passes a GetMetaFields result that is either null — returning false
// without reaching the patch — or a real NCLMetaField[]. Body-identical on 27.0 and 28.4
// (compare_symbols, bodyChanged: false, on both NavRecord.AreFieldsLoaded and
// RecordImplementation.AreFieldsLoaded).
//
// That is a property of today's CALLERS, not of the code, and nothing enforced it. PR #3856
// measured the cost of trusting exactly that kind of reading: guards-need-a-third-state.md
// had recorded #3361 part 2 as unreachable with a named backstop, three independent readers
// credited the backstop, and it turned out not to cover the shape that occurs. Its operative
// line — a neighbour is evidence only when you have executed the guard on the input you are
// claiming it covers — is what these tests do: they CALL the guard with null and assert the
// refusal, rather than arguing the call cannot happen.
//
// ── WHY BcShapeGapException AND NOT RunnerOutOfScopeException ────────────────────────────
// BcShapeGapException.cs's header draws the line on whether the runner OBTAINED the
// information. A null field list is a value the runner was handed and cannot interpret —
// the same case as the non-enumerable value the very next line already refuses through
// BcShape.RequiredEnumerable. It is not a scope claim: partial records are in scope and
// implemented. And a shape gap cannot be absorbed by an `expect-oos` manifest entry, which
// matters because whether BC hands a null here is a property of the BC build on disk.
//
// ── THE SHAPE, NOT THE REPORTED LINE ─────────────────────────────────────────────────────
// RequiredEnumerable had seven call sites. Six handle null BEFORE calling it — three with an
// explicit BcShapeGapException, three with a deliberate return/continue carrying a stated
// reason. PartialLoad.cs was the seventh and the only one answering the success value. The
// fix puts the refusal in RequiredEnumerable itself, so the helper can no longer be reached
// with a null that produces a NullReferenceException from `value.GetType()` naming nothing.
// Class 2 below pins that root fix; the six existing sites keep their own earlier handling
// and are unaffected, which class 2's last test asserts directly.
//
// ── WHY THESE LIVE IN AlRunner.Tests AND NOT THE UPSTREAM CORPUS ─────────────────────────
// Nothing here is a claim about Business Central. No AL statement can hand BC's own
// AreFieldsLoaded a null field list — that is the whole point of the unreachability finding
// above — so a service tier has nothing to adjudicate. The subject is the runner's own
// refusal contract, which bc-behavior-tests-go-upstream.md classifies as runner-specific.
// Same reasoning as BcShapeGapConventionTests and VirtualTableRefusalClaimTests.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class PartialLoadFieldListRefusalTests
{
    private const string Surface = "Record.AreFieldsLoaded (partial records)";
    private const string Member = "RecordImplementation.AreFieldsLoaded(fields)";

    // ══ 1. The patch's own null arm refuses ═══════════════════════════════════════════════
    //
    // The patch reads BC internals off `self.GetType()` before it looks at `fields`, so these
    // drive the interpretation step through the same helper the patch calls, with the same
    // surface and member strings the patch passes. That keeps the assertion about the runner's
    // refusal contract rather than about a BC type being loadable in a unit-test process.

    [Fact]
    public void NullFieldList_Refuses_RatherThanAnsweringTrue()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => BcShape.RequiredEnumerable(
                null!, Member, Surface, "the runner cannot tell which fields were asked about"));

        Assert.Equal(Surface, ex.Surface);
        Assert.Equal(Member, ex.Member);
        Assert.Contains("read as null", ex.Message, StringComparison.Ordinal);
        Assert.Contains("the runner cannot tell which fields were asked about",
            ex.Message, StringComparison.Ordinal);
    }

    // The refusal must name the surface and member in the MESSAGE, not only in the properties:
    // the reporter and the expectations manifest see the rendered text, and a message that
    // names neither sends a reader to no particular line.
    [Fact]
    public void NullRefusal_MessageNamesTheSurfaceAndTheMember()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => BcShape.RequiredEnumerable(null!, Member, Surface, "detail text"));

        Assert.StartsWith(BcShapeGapException.Prefix, ex.Message, StringComparison.Ordinal);
        Assert.Contains(Surface, ex.Message, StringComparison.Ordinal);
        Assert.Contains(Member, ex.Message, StringComparison.Ordinal);
    }

    // guards-need-a-third-state.md's constraint, asserted rather than assumed: the fix must not
    // trade a false green for a false red. A field list that IS enumerable — including a
    // legitimately EMPTY one, which BC's own body treats as "all loaded" — keeps answering.
    [Fact]
    public void EmptyFieldList_StillEnumerates_AndIsNotRefused()
    {
        var empty = new object[0];

        var result = BcShape.RequiredEnumerable(empty, Member, Surface, "detail text");

        Assert.Same(empty, result);
        Assert.Empty(result.Cast<object>());
    }

    [Fact]
    public void NonEmptyFieldList_StillEnumerates_AndIsNotRefused()
    {
        var fields = new object[] { "FieldA", "FieldB" };

        var result = BcShape.RequiredEnumerable(fields, Member, Surface, "detail text");

        Assert.Equal(new object[] { "FieldA", "FieldB" }, result.Cast<object>().ToArray());
    }

    // The three-state discrimination, all three arms in one place so a later edit that collapses
    // two of them fails here. Absent (null) and uninterpretable are BOTH refusals and must stay
    // DISTINGUISHABLE by message; present-and-enumerable stays the pass.
    [Fact]
    public void NullAndNonEnumerable_BothRefuse_ButSayDifferentThings()
    {
        var onNull = Assert.Throws<BcShapeGapException>(
            () => BcShape.RequiredEnumerable(null!, Member, Surface, "detail text"));
        var onOpaque = Assert.Throws<BcShapeGapException>(
            () => BcShape.RequiredEnumerable(42, Member, Surface, "detail text"));

        Assert.Contains("read as null", onNull.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("read as null", onOpaque.Message, StringComparison.Ordinal);
        Assert.Contains("Int32", onOpaque.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be enumerated", onOpaque.Message, StringComparison.Ordinal);

        // And the pass arm, so "everything refuses" cannot satisfy this test.
        Assert.Empty(BcShape.RequiredEnumerable(
            new object[0], Member, Surface, "detail text").Cast<object>());
    }

    // A null must NOT come back as the NullReferenceException that `value.GetType()` produced
    // before the fix. That exception names no surface, no member and no remedy, and on an
    // AL-entered path MethodScopePatches.NavMethodScope_AssertError's unfiltered catch would
    // swallow it — the inversion BcShapeGapException.cs's header documents at #3046.
    [Fact]
    public void NullRefusal_IsNotABareNullReferenceException()
    {
        var ex = Record.Exception(
            () => BcShape.RequiredEnumerable(null!, Member, Surface, "detail text"));

        Assert.NotNull(ex);
        Assert.IsNotType<NullReferenceException>(ex);
        Assert.IsType<BcShapeGapException>(ex);
    }

    // ══ 2. The patch source no longer carries the silent default ══════════════════════════
    //
    // Classes 1's assertions are about the helper. This one is about the CALL SITE: the arm
    // the issue names must be gone from RecordPatches.PartialLoad.cs, and it must be gone by
    // being routed through the refusing helper rather than by being deleted outright (which
    // would leave the null reaching `foreach` as a NullReferenceException instead).

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    // A source-reading assertion whose file is missing or empty would pass VACUOUSLY — the
    // absent thing reading as the success answer, which is the defect class these tests are
    // about. So the read refuses rather than returning "" (guards-need-a-third-state.md).
    private static string ReadPatch(string fileName)
    {
        var path = Path.Combine(RepoRoot, "AlRunner", "Patches", fileName);
        Assert.True(File.Exists(path), $"patch source not found, so nothing was measured: {path}");
        var src = File.ReadAllText(path);
        Assert.False(string.IsNullOrWhiteSpace(src), $"patch source is empty, so nothing was measured: {path}");
        return src;
    }

    private static string PartialLoadSource() => ReadPatch("RecordPatches.PartialLoad.cs");

    [Fact]
    public void PatchSource_NoLongerReturnsTrueForANullFieldList()
    {
        var src = PartialLoadSource();

        Assert.DoesNotContain("if (fields is null) return true;", src, StringComparison.Ordinal);
        Assert.DoesNotContain("if (fields == null) return true;", src, StringComparison.Ordinal);
    }

    [Fact]
    public void PatchSource_StillRoutesTheFieldListThroughTheRefusingHelper()
    {
        var src = PartialLoadSource();

        Assert.Contains("BcShape.RequiredEnumerable(", src, StringComparison.Ordinal);
        Assert.Contains(Member, src, StringComparison.Ordinal);
    }

    // The unfetched-record short-circuit is BC's own and is NOT what this change is about.
    // It must survive: corpus test PartialLoad_AreFieldsLoaded_BeforeFetch_ReturnsFalse rests
    // on it, and turning it into a refusal would red that test.
    [Fact]
    public void PatchSource_KeepsBcsOwnUnfetchedBufferShortCircuit()
    {
        var src = PartialLoadSource();

        Assert.Contains("return false;", src, StringComparison.Ordinal);
        Assert.Contains("mutableRecordBuffer", src, StringComparison.Ordinal);
    }

    // The six pre-existing RequiredEnumerable call sites each handle null themselves, before
    // the helper is reached. The root fix must not have removed that: their own handling is
    // what distinguishes "BC legitimately has no rows" (a pass, per
    // guards-need-a-third-state.md's constraint) from "the member moved".
    [Theory]
    [InlineData("RecordPatches.ObjectMetadataSystemTable.cs")]
    [InlineData("RecordPatches.PageControlFieldFromBcDocument.cs")]
    [InlineData("RecordPatches.QueryProjection.cs")]
    [InlineData("RowVersionPatches.SystemIdIntegrity.cs")]
    [InlineData("RecordPatches.InstallBaseline.cs")]
    public void SiblingCallSites_StillHandleNullBeforeReachingTheHelper(string fileName)
    {
        var src = ReadPatch(fileName);

        Assert.Contains("RequiredEnumerable", src, StringComparison.Ordinal);
        Assert.Contains("== null", src, StringComparison.Ordinal);
    }
}
