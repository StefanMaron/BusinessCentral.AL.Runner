// TestPageValidationErrorsTests — contract tests for AlRunner.TestPageValidationErrors, the
// PAGE-level ledger behind AL's TestPage.ValidationErrorCount() / GetValidationError(Index)
// (issue #3009).
//
// WHAT IS PINNED HERE AND WHAT IS NOT
//
// The BC claim — that a control write a part's own validation refuses leaves
// `Part.ValidationErrorCount() > 0`, that `GetValidationError(1)` carries that message, and
// that `GetValidationError(0)` raises a CATCHABLE AL error — is a statement about Business
// Central and lives upstream, not here (.claude/rules/bc-behavior-tests-go-upstream.md). It is
// already adjudicated: corpus codeunit 60346 "Test TestPart"
// (StefanMaron/BusinessCentral.AL.Language.Tests, PR #227, merged 0bbe3765) asserts all four
// arms on eight BC Cloud legs. This file pins the runner-side MECHANISM those claims need.
//
// RED/GREEN, measured against corpus master d025203 with the runner at this branch's parent:
//
//   TestPart_ValidationErrorCount_IsZeroOnACleanPart            PASS -> PASS  (the control arm)
//   TestPart_ValidationErrorCount_CountsARefusedFieldValidation FAIL -> PASS
//   TestPart_GetValidationError_IsOneBasedAndCarriesTheMessage  FAIL -> PASS
//   TestPart_GetValidationError_ErrorsOnIndexZero               FAIL -> PASS
//
// The first of those is why the fix cannot be "return 1": it passed BEFORE the fix, against
// the hardcoded 0, and has to keep passing after it.
//
// THE ONE PLACE THIS LEDGER DELIBERATELY DIFFERS FROM THE FIELD LEDGER is the exception an
// out-of-range read raises, and it is a real BC asymmetry rather than a tidy-up. Both AL
// boundaries subtract 1 and translate, but they catch different types (unmodified Ncl.dll,
// 28.1):
//
//     NavTestField.ALGetValidationError(index)     catch (IndexOutOfRangeException)
//     NavTestPageBase.ALGetValidationError(index)  catch (ArgumentOutOfRangeException)
//
// Enumerable.ElementAt raises the ARGUMENT flavour, so the field's catch is dead (measured,
// corpus run 34002487601: the exception escapes as "Unexpected CLR exception thrown." and AL
// asserterror does not trap it) while the page's catch is LIVE.
//
// WHICH LAYER PROVES WHICH, because it is easy to overclaim here. The corpus test asserts
// `GetLastErrorText() <> ''`, so it pins that index 0 is OUT of the 1-based range — the
// off-by-one that matters, and the assertion a 0-based ledger fails. It does NOT discriminate
// the exception flavours: probed on this branch, an IndexOutOfRangeException implementation
// passes it too, because the runner surfaces that as a trappable AL error as well. The flavour
// is pinned HERE, against the decompiled catch above, and nowhere else.
using System;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageValidationErrorsTests
{
    [Fact]
    public void ACleanPage_ReportsZeroAndHasNothingToRead()
    {
        var page = new TestPageValidationErrors();

        Assert.Equal(0, page.Count);
        // Nothing recorded, so index 0 is already past the end. This is the arm that makes the
        // "always report one error" implementation fail.
        Assert.Throws<ArgumentOutOfRangeException>(() => page.Get(0));
    }

    [Fact]
    public void ARecordedRefusal_IsCountedAndReadableAtZeroBasedIndexZero()
    {
        var page = new TestPageValidationErrors();
        page.Record("ALT TestPart Line refuses the grade BADGRADE");

        Assert.Equal(1, page.Count);
        // BC's AL boundary has already subtracted 1, so AL's GetValidationError(1) arrives here
        // as Get(0). The exact text, not merely non-empty: an implementation that stored a
        // placeholder would pass a non-empty assertion.
        Assert.Equal("ALT TestPart Line refuses the grade BADGRADE", page.Get(0));
    }

    [Fact]
    public void SeveralRefusals_KeepTheOrderTheyWereRecordedIn()
    {
        var page = new TestPageValidationErrors();
        page.Record("first");
        page.Record("second");
        page.Record("third");

        Assert.Equal(3, page.Count);
        Assert.Equal("first", page.Get(0));
        Assert.Equal("second", page.Get(1));
        Assert.Equal("third", page.Get(2));
    }

    [Fact]
    public void ReadingIsNotAConsume_UnlikeTheFieldLedger()
    {
        // ITestPage carries no LastUsedValidationErrorId / MaxValidationErrorId at all — BC's
        // "is there something new since the snapshot" test has no page-level counterpart — so a
        // read must be repeatable rather than marking anything used. Verified against 28.1:
        // search_members("ValidationErrorId") filtered to ITestPage returns nothing.
        var page = new TestPageValidationErrors();
        page.Record("only");

        Assert.Equal("only", page.Get(0));
        Assert.Equal("only", page.Get(0));
        Assert.Equal(1, page.Count);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(5)]
    public void ReadingOutOfRange_RaisesTheFlavourTheAlBoundaryActuallyCatches(int index)
    {
        // ArgumentOutOfRangeException, NOT IndexOutOfRangeException. This is the whole reason
        // Get uses Enumerable.ElementAt rather than the list indexer: it is the call BC's own
        // client makes, and NavTestPageBase.ALGetValidationError(int) catches precisely this
        // type and turns it into a NavNCLIndexOutOfBoundsException that AL asserterror traps.
        // The corpus test does not catch a wrong flavour (see this file's header), so this
        // assertion is the only thing standing between the runner and a page-side chain that
        // merely looks right. Probed both ways on this branch: swapping ElementAt for an
        // indexer + IndexOutOfRangeException keeps the corpus green and turns these four red.
        var page = new TestPageValidationErrors();
        page.Record("only");
        Assert.Equal("only", page.Get(0));

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => page.Get(index));
        Assert.Equal("index", ex.ParamName);
    }

    [Fact]
    public void AControlRefusal_LandsInBothTheFieldLedgerAndThePageLedger()
    {
        // The invariant that keeps the two views from drifting: TestFieldValidationErrors.Record
        // is the single write route, and it feeds both. Real BC keeps one client-side error list
        // per page of which the control's is a view, so a refusal counted on the control and not
        // on the page (or the reverse) would be a state split BC does not have.
        var page = new TestPageValidationErrors();
        var field = new TestFieldValidationErrors(page);

        field.Record("Deliberate OnValidate failure for VAL-1", appendRefreshSuffix: true);

        Assert.Equal(1, field.Count);
        Assert.Equal(1, page.Count);
        // The SAME stored text, refresh suffix included — the page does not re-derive it.
        Assert.Equal("Deliberate OnValidate failure for VAL-1" + TestFieldValidationErrors.RefreshSuffix,
            field.Get(0));
        Assert.Equal("Deliberate OnValidate failure for VAL-1" + TestFieldValidationErrors.RefreshSuffix,
            page.Get(0));
    }

    [Fact]
    public void TwoControlsOnOnePage_AccumulateOnThatOnePageInWriteOrder()
    {
        // A page's count is the page's, not any one control's. Each control still reports only
        // its own — which is what BC's NavTestField.CheckError arithmetic subtracts to decide
        // whether the PAGE gained an error the written control did not raise.
        var page = new TestPageValidationErrors();
        var a = new TestFieldValidationErrors(page);
        var b = new TestFieldValidationErrors(page);

        a.Record("control A refused", appendRefreshSuffix: false);
        b.Record("control B refused", appendRefreshSuffix: false);

        Assert.Equal(1, a.Count);
        Assert.Equal(1, b.Count);
        Assert.Equal(2, page.Count);
        Assert.Equal("control A refused", page.Get(0));
        Assert.Equal("control B refused", page.Get(1));
    }

    [Fact]
    public void AFieldWithNoPage_RecordsOnlyOnItself()
    {
        // The record-only LiveNavTestField ctor and the degraded MockITestPage path have no page
        // ledger to report to. That must be a null sink rather than a crash.
        var field = new TestFieldValidationErrors(null);

        field.Record("refused", appendRefreshSuffix: false);

        Assert.Equal(1, field.Count);
        Assert.Equal("refused", field.Get(0));
    }

    [Fact]
    public void RunRecordingRefusal_FeedsThePageLedgerToo()
    {
        // The route a real control write takes. A BC/AL error is recorded on both ledgers rather
        // than escaping, so BC's own CheckError can raise it afterwards and AL can still read
        // either count once the asserterror has trapped it.
        var page = new TestPageValidationErrors();
        var field = new TestFieldValidationErrors(page);

        field.RunRecordingRefusal(
            () => throw new Microsoft.Dynamics.Nav.Types.Exceptions.NavNCLDialogException(
                      "ALT TestPart Line refuses the grade BADGRADE"),
            appendRefreshSuffix: false);

        Assert.Equal(1, page.Count);
        Assert.Equal("ALT TestPart Line refuses the grade BADGRADE", page.Get(0));
    }

    [Fact]
    public void ALoudRunnerRefusal_IsNotRecordedOnThePageEither()
    {
        // .claude/rules/loud-failures.md, extended to the new ledger: a RunnerOutOfScopeException
        // is not a validation error and must tear straight through. Recording it on the page
        // would let AL's asserterror absorb a loud refusal and read as a green test — the same
        // silent-failure shape the field ledger already refuses, now checked on the half this
        // change added.
        var page = new TestPageValidationErrors();
        var field = new TestFieldValidationErrors(page);

        Assert.Throws<AlRunner.Infrastructure.RunnerOutOfScopeException>(
            () => field.RunRecordingRefusal(
                      () => throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                                "NavEmail.Send", "email-smtp — see docs/scope.md#email"),
                      appendRefreshSuffix: false));

        Assert.Equal(0, page.Count);
        Assert.Equal(0, field.Count);
    }
}
