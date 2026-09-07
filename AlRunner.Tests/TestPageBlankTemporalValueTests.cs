// TestPageBlankTemporalValueTests — issue #2361 (a blank Date/Time/DateTime read through a
// TestPage control rendered as 01/01/0001 00:00:00 where real BC renders '').
//
// RUNNER-MECHANISM test, not a claim about what real BC does. It pins the predicate
// TestPageBlankTemporalValue applies — "blank" is exactly NavDateTimeValue.IsZeroOrEmpty, i.e.
// the wrapped DateTime equal to default(DateTime) — and, just as importantly, that it declines
// on everything else so the caller's existing rendering chain still runs.
//
// The BEHAVIOURAL claim (what a blank vs populated temporal control reads as on a real service
// tier) is proven upstream in the corpus: codeunit 60662 "TP Blank Temporal Tests", merged as
// StefanMaron/BusinessCentral.AL.Language.Tests#154, with the typed-AssertEquals arm added in
// #267. Per bc-behavior-tests-go-upstream.md that is where it belongs; this file exists so a
// regression in OUR OWN predicate fails in milliseconds without the BC engine loaded twice.
//
// Both entry points are covered because they are reached by different callers and neither
// implies the other: Format() sees the stored NavValue (the Value getter's path), FormatObject()
// sees the already-unwrapped CLR value (the ValueToString path BC's ALAssertEquals drives for a
// TYPED expected argument). Deleting the FormatObject half left every pre-existing corpus test
// passing, which is why the typed-argument tests were added upstream — and why it is asserted
// separately here.

using System.Globalization;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageBlankTemporalValueTests
{
    // ---------------------------------------------------------------- Format (the NavValue path)

    [Fact]
    public void Format_BlankNavDateTime_AnswersTheEmptyString()
    {
        var blank = NavDateTime.Create(default(DateTime));

        Assert.Equal(string.Empty, TestPageBlankTemporalValue.Format(blank));
    }

    [Fact]
    public void Format_BlankNavDate_AnswersTheEmptyString()
    {
        var blank = NavDate.Create(default(DateTime));

        Assert.Equal(string.Empty, TestPageBlankTemporalValue.Format(blank));
    }

    [Fact]
    public void Format_BlankNavTime_AnswersTheEmptyString()
    {
        var blank = NavTime.Create(default(DateTime));

        Assert.Equal(string.Empty, TestPageBlankTemporalValue.Format(blank));
    }

    // The guard on all three above: a POPULATED temporal must be declined (null), not blanked,
    // so the caller falls through to its normal rendering. An implementation answering "" for
    // every temporal would satisfy the blank cases and fail here — that mutation was run and is
    // caught by the corpus suite's populated arm too.
    [Fact]
    public void Format_PopulatedNavDateTime_DeclinesSoTheCallerRendersIt()
    {
        var populated = NavDateTime.Create(
            DateTime.SpecifyKind(new DateTime(2024, 3, 17, 14, 30, 0), DateTimeKind.Local));

        Assert.Null(TestPageBlankTemporalValue.Format(populated));
    }

    [Fact]
    public void Format_PopulatedNavDate_DeclinesSoTheCallerRendersIt()
    {
        var populated = NavDate.Create(
            DateTime.SpecifyKind(new DateTime(2024, 3, 17), DateTimeKind.Local));

        Assert.Null(TestPageBlankTemporalValue.Format(populated));
    }

    // A non-temporal NavValue must be declined too, or this helper would swallow values that
    // belong to the Option/Numeric/Boolean arms sitting beside it in the same chain.
    [Fact]
    public void Format_ANonTemporalNavValue_IsDeclined()
    {
        Assert.Null(TestPageBlankTemporalValue.Format(NavText.Create("not a temporal")));
        Assert.Null(TestPageBlankTemporalValue.Format(NavInteger.Create(0)));
    }

    [Fact]
    public void Format_Null_IsDeclined()
        => Assert.Null(TestPageBlankTemporalValue.Format(null));

    // ------------------------------------------------------- FormatObject (the ValueToString path)

    // BC's NavTestField.ALAssertEquals renders a TYPED expected value through ValueToString and
    // compares it ordinally against the control, so this half has to agree with Format above or
    // AssertEquals(<a blank DateTime variable>) can never match. All three temporal types reach
    // it as a bare DateTime, because NavDate, NavTime and NavDateTime all derive from
    // NavDateTimeValue, whose ClientObject hands back the wrapped DateTime.
    [Fact]
    public void FormatObject_DefaultDateTime_AnswersTheEmptyString()
        => Assert.Equal(string.Empty, TestPageBlankTemporalValue.FormatObject(default(DateTime)));

    [Fact]
    public void FormatObject_APopulatedDateTime_DeclinesSoTheCallerRendersIt()
        => Assert.Null(TestPageBlankTemporalValue.FormatObject(new DateTime(2024, 3, 17, 14, 30, 0)));

    // BC's smallest representable Date is DateTimeMinimum = 0001-01-02, one day after the CLR
    // minimum, so a real temporal can never BE default(DateTime). This pins that the predicate
    // stops at the CLR default and does not blank the value one tick above it — which is what
    // makes suppressing default(DateTime) lossless rather than a guess about a boundary.
    [Fact]
    public void FormatObject_BcsSmallestRepresentableDate_IsNotTreatedAsBlank()
    {
        var bcMinimum = new DateTime(1, 1, 2);

        Assert.Null(TestPageBlankTemporalValue.FormatObject(bcMinimum));
        Assert.Null(TestPageBlankTemporalValue.FormatObject(default(DateTime).AddTicks(1)));
    }

    [Fact]
    public void FormatObject_ANonDateTimeValue_IsDeclined()
    {
        Assert.Null(TestPageBlankTemporalValue.FormatObject("01/01/0001 00:00:00"));
        Assert.Null(TestPageBlankTemporalValue.FormatObject(0));
        Assert.Null(TestPageBlankTemporalValue.FormatObject(null));
    }

    // The two halves must agree on the SAME value, since one renders the control and the other
    // renders the expected argument compared against it. Asserted directly rather than left to
    // follow from the pairs above.
    [Fact]
    public void FormatAndFormatObject_AgreeOnABlankAndOnAPopulatedValue()
    {
        var populated = DateTime.SpecifyKind(new DateTime(2024, 3, 17, 14, 30, 0), DateTimeKind.Local);

        Assert.Equal(
            TestPageBlankTemporalValue.Format(NavDateTime.Create(default(DateTime))),
            TestPageBlankTemporalValue.FormatObject(default(DateTime)));

        Assert.Equal(
            TestPageBlankTemporalValue.Format(NavDateTime.Create(populated)),
            TestPageBlankTemporalValue.FormatObject(populated));
    }

    // What the runner used to answer, kept as a literal so the regression is named rather than
    // implied: this is the string issue #2361 reports reaching AL, and the one BC's own
    // formatters suppress via `if (navX.IsZeroOrEmpty) return string.Empty;`.
    [Fact]
    public void TheOldRendering_IsWhatFormatObjectNowSuppresses()
    {
        Assert.Equal(
            "01/01/0001 00:00:00",
            Convert.ToString(default(DateTime), CultureInfo.InvariantCulture));

        Assert.Equal(string.Empty, TestPageBlankTemporalValue.FormatObject(default(DateTime)));
    }
}
