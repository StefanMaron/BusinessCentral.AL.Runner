// TestFieldValidationErrorsTests — contract tests for AlRunner.TestFieldValidationErrors,
// the ledger a TestPage control keeps of the writes it refused (issue #2900).
//
// WHAT IS PINNED HERE AND WHAT IS NOT
//
// The BC claim — that `asserterror TestPage.Field.SetValue(x)` leaves
// `Field.ValidationErrorCount() = 1` and `Field.GetValidationError(1)` equal to the bare error
// text — is a statement about Business Central and belongs upstream, not here
// (.claude/rules/bc-behavior-tests-go-upstream.md). It is filed as corpus codeunit
// "TVE Validation Error Tests"; this file pins the runner-side MECHANISM that has to be right
// for that claim to hold, so a regression is caught in milliseconds without the BC engine.
//
// The two halves the runner owns:
//
//   1. The ledger's own arithmetic — count, id assignment, and the "consume" that stops BC's
//      NavTestField.CheckError from re-raising an error it has already reported.
//   2. WHICH exceptions become a recorded validation error. Only BC/AL errors
//      (NavNCLException) do. A RunnerOutOfScopeException must tear straight through: recording
//      it would let AL's `asserterror` absorb a loud refusal and read as a green test, which is
//      the silent-failure shape .claude/rules/loud-failures.md exists to prevent.
//
// RED/GREEN: before this change ValidationErrorCount and GetValidationError were hardcoded
// `=> 0` / `=> string.Empty` on both LiveNavTestField and PageVariableTestField, so every test
// below that asserts a non-zero count or a recovered message fails against that implementation,
// and Microsoft's own Codeunit134614.TestRemoveSUPERPermissionsByUserAll failed on
// `Assert.AreEqual(1, …ValidationErrorCount())` reading 0.
//
// TWO VALUES HERE WERE CORRECTED BY A SERVICE TIER, NOT DERIVED. Corpus run 34002487601
// measured what BC really stores and what it really does past the end of the ledger; the first
// draft of this file asserted a bare message and an IndexOutOfRangeException, and both were
// wrong. See TestFieldValidationErrors' header for the measurement and the binding split.
using System;
using AlRunner;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Types.Exceptions;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestFieldValidationErrorsTests
{
    private static NavNCLDialogException AlError(string message)
    {
        // The same type AL's Error() surfaces as — see ErrorClassifier's class comment.
        var t = Type.GetType(
            "Microsoft.Dynamics.Nav.Types.Exceptions.NavNCLDialogException, Microsoft.Dynamics.Nav.Types");
        Assert.NotNull(t);
        var ctor = t!.GetConstructor(new[] { typeof(string) });
        Assert.NotNull(ctor);
        return (NavNCLDialogException)ctor!.Invoke(new object[] { message });
    }

    // ── the ledger's arithmetic ──────────────────────────────────────────────

    [Fact]
    public void AFreshLedger_CountsNothingAndHasNoIds()
    {
        var errors = new TestFieldValidationErrors();

        Assert.Equal(0, errors.Count);
        Assert.Equal(0L, errors.MaxId);
        Assert.Equal(0L, errors.LastUsedId);
    }

    [Fact]
    public void RecordingAnError_RaisesMaxIdAboveLastUsed()
    {
        // This inequality IS the trigger: BC's NavTestField.CheckError snapshots
        // LastUsedValidationErrorId before the write and raises NavTestValidationException when
        // MaxValidationErrorId has moved past it afterwards. Equal ids mean no exception, so a
        // ledger that recorded without moving MaxId would swallow the refusal entirely.
        var errors = new TestFieldValidationErrors();

        errors.Record("There should be at least one enabled 'SUPER' user.", appendRefreshSuffix: false);

        Assert.Equal(1, errors.Count);
        Assert.Equal(1L, errors.MaxId);
        Assert.Equal(0L, errors.LastUsedId);
        Assert.True(errors.MaxId > errors.LastUsedId);
    }

    [Fact]
    public void ReadingAnError_ReturnsItVerbatimAndConsumesItsId()
    {
        var errors = new TestFieldValidationErrors();
        errors.Record("There should be at least one enabled 'SUPER' user.", appendRefreshSuffix: false);

        Assert.Equal("There should be at least one enabled 'SUPER' user.", errors.Get(0));

        // Consumed: BC's own throw path calls GetValidationError, so the NEXT CheckError —
        // the `.Value` getter, say — must not re-raise the error BC has already reported.
        Assert.Equal(1L, errors.LastUsedId);
        Assert.False(errors.MaxId > errors.LastUsedId);
    }

    [Fact]
    public void SeveralErrors_KeepTheirOrderAndConsumeUpToTheOneRead()
    {
        var errors = new TestFieldValidationErrors();
        errors.Record("first", appendRefreshSuffix: false);
        errors.Record("second", appendRefreshSuffix: false);
        errors.Record("third", appendRefreshSuffix: false);

        Assert.Equal(3, errors.Count);
        Assert.Equal(3L, errors.MaxId);
        Assert.Equal("first", errors.Get(0));
        Assert.Equal("third", errors.Get(2));
        Assert.Equal(3L, errors.LastUsedId);

        // Reading an OLDER one afterwards must not un-consume the newer id, or BC would
        // re-raise an error it already reported.
        Assert.Equal("second", errors.Get(1));
        Assert.Equal(3L, errors.LastUsedId);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(5)]
    public void ReadingOutOfRange_RaisesTheSameExceptionTheTierRaises(int index)
    {
        // MEASURED, corpus run 34002487601, not derived. BC's client does
        // System.Linq.Enumerable.ElementAt, so an out-of-range read raises
        // ArgumentOutOfRangeException(index) — which NavTestField.ALGetValidationError's
        // `catch (IndexOutOfRangeException)` does NOT match, so it escapes to the test framework
        // as "Unexpected CLR exception thrown." and AL `asserterror` does not trap it.
        //
        // The first draft of this test asserted IndexOutOfRangeException, reasoning that BC
        // would not carry a catch for something unreachable. The tier says the catch IS
        // unreachable. Asserting the Argument flavour is what keeps the runner from inventing a
        // trappable AL error the tier never produces.
        //
        // One error is recorded, so index 0 is the ONLY valid one; -1, 1 and 5 are all out.
        var errors = new TestFieldValidationErrors();
        errors.Record("only", appendRefreshSuffix: false);
        Assert.Equal("only", errors.Get(0));

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => errors.Get(index));
        Assert.Equal("index", ex.ParamName);
    }

    // ── which exceptions become a validation error ───────────────────────────

    [Fact]
    public void ASuccessfulWrite_RecordsNothing()
    {
        var errors = new TestFieldValidationErrors();
        var ran = false;

        errors.RunRecordingRefusal(() => ran = true, appendRefreshSuffix: true);

        Assert.True(ran);
        Assert.Equal(0, errors.Count);
        Assert.Equal(0L, errors.MaxId);
    }

    [Fact]
    public void AnAlError_OnARecBoundControl_IsRecordedWithBcsRefreshSuffix()
    {
        // The whole point: BC's ITestField contract is "the setter records, BC raises". A setter
        // that throws leaves ValidationErrorCount at 0 and BC with nothing to wrap.
        //
        // The suffix is MEASURED (corpus run 34002487601): a Rec-bound control's OnValidate
        // raising `Error('Deliberate OnValidate failure for VAL-1')` is stored by real BC as
        // that text plus " (Select Refresh to discard errors)". The first draft recorded the
        // bare message and the tier disagreed on all reporting legs.
        var errors = new TestFieldValidationErrors();

        errors.RunRecordingRefusal(
            () => throw AlError("Deliberate OnValidate failure for VAL-1"),
            appendRefreshSuffix: true);

        Assert.Equal(1, errors.Count);
        Assert.Equal("Deliberate OnValidate failure for VAL-1 (Select Refresh to discard errors)",
            errors.Get(0));
    }

    [Fact]
    public void AnAlError_OnAPageVariableControl_IsRecordedBare()
    {
        // A page-global control stages no row edit, so there is nothing for "Refresh to discard"
        // to discard. Microsoft's Tests-SINGLESERVER Codeunit134614 asserts exactly this, with
        // exact equality, for a control verified to be page-variable-bound on page 9816
        // "Permission Set by User" (an earlier version of this comment said 9807, which is
        // "User Card").
        //
        // This no longer rests on Microsoft's assertion alone: corpus PR #184 asked a real
        // service tier and merged 2026-09-06 green on all eight BC Cloud legs (run 34016443056),
        // so corpus codeunit 60808 now states it. Pinning it here means a change of mind has to
        // be deliberate.
        var errors = new TestFieldValidationErrors();

        errors.RunRecordingRefusal(
            () => throw AlError("There should be at least one enabled 'SUPER' user."),
            appendRefreshSuffix: false);

        Assert.Equal(1, errors.Count);
        Assert.Equal("There should be at least one enabled 'SUPER' user.", errors.Get(0));
        Assert.DoesNotContain("Select Refresh", errors.Get(0));
    }

    [Fact]
    public void AnOutOfScopeRefusal_TearsThroughAndIsNotRecorded()
    {
        var errors = new TestFieldValidationErrors();

        var ex = Assert.Throws<RunnerOutOfScopeException>(
            () => errors.RunRecordingRefusal(
                () => throw new RunnerOutOfScopeException("TestPage control 42", "testpage-x"),
                appendRefreshSuffix: true));

        Assert.Equal("TestPage control 42", ex.Api);
        Assert.Equal(0, errors.Count);
    }

    [Fact]
    public void AnOutOfScopeMessageOnABcException_TearsThroughAndIsNotRecorded()
    {
        // The Cecil-injected throw sites raise the "out-of-scope: <api> — <reason>" convention
        // as a NavNCLDialogException, which tests/expectations/ matches on. Recording it would
        // bury an out-of-scope signal inside BC's validation wrapper.
        var errors = new TestFieldValidationErrors();

        var ex = Assert.Throws<NavNCLDialogException>(
            () => errors.RunRecordingRefusal(
                () => throw AlError(
                    "out-of-scope: NavReport.SaveAs — report-rendering — see docs/scope.md#report-rendering"),
                appendRefreshSuffix: true));

        Assert.Contains("out-of-scope: NavReport.SaveAs", ex.Message);
        Assert.Equal(0, errors.Count);
    }

    [Fact]
    public void ARunnerBug_TearsThroughAndIsNotRecorded()
    {
        // A NullReferenceException from the runner's own code is not a BC validation error.
        // Recording it would present a runner defect to AL as a refusal the page made.
        var errors = new TestFieldValidationErrors();

        Assert.Throws<NullReferenceException>(
            () => errors.RunRecordingRefusal(() => throw new NullReferenceException("runner bug"),
                appendRefreshSuffix: true));

        Assert.Equal(0, errors.Count);
    }

    // ── the message BC composes from what was recorded ───────────────────────

    [Fact]
    public void BcWrapsTheRecordedText_InTheShapeTheCorpusMeasured()
    {
        // Not an assumption: Lang.TestValidationException reads
        // "Validation error for Field: {0},  Message = '{1}'" in
        // Microsoft.Dynamics.Nav.Language.dll on the 28.1 artifact, and corpus PR #163 measured
        // the resulting string on all eight BC legs. This test asserts that BC's own
        // NavTestValidationException.Create — the call NavTestField.CheckError makes with the
        // text the ledger recorded — still produces it, so a BC-side change to that resource
        // shows up here rather than as a mystery in an AL suite.
        var create = Type.GetType(
                "Microsoft.Dynamics.Nav.Types.Exceptions.NavTestValidationException, Microsoft.Dynamics.Nav.Types")
            ?.GetMethod("Create", new[] { typeof(System.Globalization.CultureInfo), typeof(string), typeof(string) });
        Assert.NotNull(create);

        var ex = (Exception)create!.Invoke(null, new object?[]
        {
            System.Globalization.CultureInfo.InvariantCulture,
            "Rec True",
            // The inner text as the ledger now stores it: the helper's core plus the suffix
            // TestFieldValidationErrors appends for a Rec-bound control.
            "Your entry of 'False' is not an acceptable value for 'Rec True'."
                + TestFieldValidationErrors.RefreshSuffix,
        })!;

        Assert.Equal(
            "Validation error for Field: Rec True,  Message = 'Your entry of 'False' is not an "
            + "acceptable value for 'Rec True'. (Select Refresh to discard errors)'",
            ex.Message);
    }
}
