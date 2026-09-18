// NonModalCloseActionShapeGapTests — issue #4363.
//
// THE DEFECT
//   RunnerModalDispatch.NonModalCloseResult answered `null` from four reflection exits that had
//   all FAILED to read something, and FormRun handed that null straight to TryQueryCloseForm.
//   There it met `Convert.ToInt32(null)`, which returns 0 WITHOUT throwing — so it did not take
//   the catch that skips the trigger, it raised BC's QueryCloseForm(0). BC's own
//   QueryCloseFormAsync casts that argument `(FormResult)closeActionValue` before handing it to
//   RaiseOnQueryClosePageAsync, so ordinal 0 reaches user-written OnQueryClosePage as
//   CloseAction::None while the method's doc comment promised OK. That is a silent wrong answer
//   on an AL-observable surface (.claude/rules/loud-failures.md).
//
// WHAT THESE TESTS ARE, AND ARE NOT
//   Runner-internal claims only: that OUR bind refuses when it cannot read BC's shape, and that
//   it still answers the OK member when it can. Whether real BC sends OK for a [PageHandler]'s
//   non-modal close is a plain BC-behaviour claim, adjudicated upstream (see the PR body); it is
//   NOT re-asserted here, and no test in this file depends on it.
//
// WHY FAKES RATHER THAN A LIVE NavForm
//   The refusal fires only on a BC shape that no provisioned artifact has — every Types.dll here
//   declares Microsoft.Dynamics.Nav.Types.FormResult with None=0 and OK=1. A fake type is the
//   only way to execute the refusal at all, which is what guards-need-a-third-state.md § "Prove
//   the third state fires" asks for.
using System;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public class NonModalCloseActionShapeGapTests
{
    // ── Fakes standing in for the shapes BC could present ───────────────────────────────

    /// <summary>What every provisioned BC actually declares: one CloseForm(FormResult).</summary>
    private enum FaithfulFormResult { None = 0, OK = 1, Cancel = 2 }

    private sealed class FaithfulForm
    {
        public void CloseForm(FaithfulFormResult formResult) { _ = formResult; }
    }

    /// <summary>A renumbered enum: OK is no longer 1. The point of reading the member by NAME.</summary>
    private enum RenumberedFormResult { None = 0, Cancel = 1, OK = 7 }

    private sealed class RenumberedForm
    {
        public void CloseForm(RenumberedFormResult formResult) { _ = formResult; }
    }

    /// <summary>BC renamed or removed CloseForm — the bind cannot be performed at all.</summary>
    private sealed class FormWithNoCloseForm
    {
        public void ShutForm(FaithfulFormResult formResult) { _ = formResult; }
    }

    /// <summary>CloseForm exists but no longer takes an enum.</summary>
    private sealed class FormWithNonEnumParameter
    {
        public void CloseForm(int formResult) { _ = formResult; }
    }

    /// <summary>CloseForm takes an enum that declares no member called OK.</summary>
    private enum FormResultWithoutOk { None = 0, Accepted = 1, Cancel = 2 }

    private sealed class FormWithEnumLackingOk
    {
        public void CloseForm(FormResultWithoutOk formResult) { _ = formResult; }
    }

    /// <summary>
    /// BC kept the NAME and dropped the parameter. The bind resolves — it filters on name only,
    /// because the parameter type is precisely what it is discovering — and the read of
    /// GetParameters()[0] is then the thing that cannot be performed.
    /// </summary>
    private sealed class FormWithParameterlessCloseForm
    {
        public void CloseForm() { }
    }

    /// <summary>
    /// BC added a second parameter. Two-parameter CloseForm is DELIBERATELY accepted: the read
    /// this method performs is of parameter 0's type, and a member appended after it does not
    /// make that read unperformable. See the arity guard's comment at the call site.
    /// </summary>
    private sealed class FormWithTwoParameterCloseForm
    {
        public void CloseForm(FaithfulFormResult formResult, bool persistData) { _ = formResult; _ = persistData; }
    }

    // ── POSITIVE: the read succeeds, and the answer is the OK MEMBER, not the ordinal ────

    [Fact]
    public void NonModalCloseResult_AnswersTheOkMember_OnTheShapeEveryProvisionedBcDeclares()
    {
        var result = AlRunner.Patches.RunnerModalDispatch.NonModalCloseResult(new FaithfulForm());

        Assert.Equal(FaithfulFormResult.OK, result);
        Assert.Equal(1, Convert.ToInt32(result));
        Assert.NotEqual(FaithfulFormResult.None, result);
    }

    /// <summary>
    /// The property the doc comment claims and the reason the value is read dynamically: a
    /// renumbered enum must still yield OK, whatever ordinal OK now carries. A hardcoded 1 would
    /// answer Cancel here, and a hardcoded 0 would answer None.
    /// </summary>
    [Fact]
    public void NonModalCloseResult_FollowsARenumberedEnum_SoTheOrdinalIsNeverAssumed()
    {
        var result = AlRunner.Patches.RunnerModalDispatch.NonModalCloseResult(new RenumberedForm());

        Assert.Equal(RenumberedFormResult.OK, result);
        Assert.Equal(7, Convert.ToInt32(result));
    }

    // ── NEGATIVE: each unmeasurable read refuses, naming which one it was ────────────────

    [Fact]
    public void NonModalCloseResult_RefusesWhenBcDeclaresNoCloseForm_RatherThanAnsweringNone()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => AlRunner.Patches.RunnerModalDispatch.NonModalCloseResult(new FormWithNoCloseForm()));

        Assert.Contains("CloseForm", ex.Message);
        Assert.Contains(BcShapeGapException.Prefix, ex.Message);

        // The AL-observable stake belongs in the message: a reader must see that the alternative
        // to refusing was a WRONG CloseAction, not a missing one.
        //
        // Asserted against BOTH structured fields separately, not against Message. Message
        // concatenates Surface and Detail, and each of them independently carries the literal, so
        // an assertion on Message alone stays green when either one loses it and cannot say which
        // string supplies the stake. These two say exactly that, and each fails on its own.
        Assert.Contains("OnQueryClosePage", ex.Surface);
        Assert.Contains("OnQueryClosePage", ex.Detail);
        Assert.Contains("OnQueryClosePage", ex.Message);
    }

    [Fact]
    public void NonModalCloseResult_RefusesWhenCloseFormsParameterIsNotAnEnum()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => AlRunner.Patches.RunnerModalDispatch.NonModalCloseResult(new FormWithNonEnumParameter()));

        Assert.Contains("CloseForm", ex.Message);
        Assert_ContainsAny(ex.Message, "Int32", "not an enum");
    }

    [Fact]
    public void NonModalCloseResult_RefusesWhenTheEnumDeclaresNoOkMember()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => AlRunner.Patches.RunnerModalDispatch.NonModalCloseResult(new FormWithEnumLackingOk()));

        Assert.Contains("OK", ex.Message);
        Assert.Contains("FormResultWithoutOk", ex.Message);
    }

    /// <summary>
    /// The mirror of the whole issue, stated as its own claim: whatever a refusal says, it must
    /// never be reachable for a caller to treat ordinal 0 as the answer. Every refusing shape
    /// throws rather than returning anything at all.
    /// </summary>
    [Fact]
    public void NonModalCloseResult_NeverAnswersOrdinalZero_OnAnyShapeItCannotRead()
    {
        foreach (var form in new object[]
                 {
                     new FormWithNoCloseForm(), new FormWithNonEnumParameter(),
                     new FormWithEnumLackingOk(), new FormWithParameterlessCloseForm(),
                 })
        {
            Assert.Throws<BcShapeGapException>(
                () => AlRunner.Patches.RunnerModalDispatch.NonModalCloseResult(form));
        }
    }

    // ── ARITY: the bind filters on NAME only, so parameter 0 may not be there ───────────

    /// <summary>
    /// The bind cannot filter on arity — BcShape.FindMethod's `types:` filter is SequenceEqual
    /// over exact parameter TYPES, and the parameter type is the very thing being discovered, so
    /// there is nothing to pass it. That leaves GetParameters()[0] as an unguarded read, and on a
    /// parameterless CloseForm it raised a bare IndexOutOfRangeException naming no surface, no
    /// member and no remedy — the exact shape this whole change exists to remove, reintroduced at
    /// its own fix site. This test pins the exception TYPE and that the message names the
    /// AL-observable surface, so the two failure modes stay distinguishable: a test asserting only
    /// "it throws" passes on the unguarded code too.
    /// </summary>
    [Fact]
    public void NonModalCloseResult_RefusesWhenCloseFormTakesNoParameter_RatherThanIndexingPastItsEnd()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => AlRunner.Patches.RunnerModalDispatch.NonModalCloseResult(
                new FormWithParameterlessCloseForm()));

        Assert.Contains("CloseForm", ex.Message);
        Assert.Contains(BcShapeGapException.Prefix, ex.Message);
        // Same stake as every other refusal here: the alternative was a WRONG CloseAction. Pinned
        // per structured field for the reason given above — Message alone cannot discriminate.
        Assert.Contains("OnQueryClosePage", ex.Surface);
        Assert.Contains("OnQueryClosePage", ex.Detail);
        // And it must say what it actually found, so a reader can tell this refusal from the
        // absent-member one above without reading the source.
        Assert.Contains("declares no parameter", ex.Message);
    }

    /// <summary>
    /// The other direction, decided deliberately rather than inherited: a TWO-parameter CloseForm
    /// resolves and answers OK. The previous arity-filtered bind returned null here, which the
    /// caller turned into CloseAction::None — so accepting it is strictly better than what it
    /// replaced, and the read being performed (parameter 0's type) is genuinely available.
    /// </summary>
    [Fact]
    public void NonModalCloseResult_AcceptsATwoParameterCloseForm_BecauseParameterZeroIsStillReadable()
    {
        var result = AlRunner.Patches.RunnerModalDispatch.NonModalCloseResult(
            new FormWithTwoParameterCloseForm());

        Assert.Equal(FaithfulFormResult.OK, result);
        Assert.Equal(1, Convert.ToInt32(result));
    }

    private static void Assert_ContainsAny(string haystack, params string[] needles)
    {
        foreach (var n in needles)
            if (haystack.Contains(n, StringComparison.Ordinal)) return;
        Assert.Fail($"none of [{string.Join(", ", needles)}] found in: {haystack}");
    }
}
