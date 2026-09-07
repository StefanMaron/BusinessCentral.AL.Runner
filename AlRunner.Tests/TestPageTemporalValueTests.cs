// TestPageTemporalValueTests — contract tests for AlRunner.TestPageTemporalValue (issue #3384).
//
// Replaces TestPageDateValueTests, which tested TestPageDateValue: the Date-only helper #2054
// added, which accepted exactly one spelling and threw RunnerOutOfScopeException for every
// other. A real BC 28.4 service tier accepts several — Format() text, ISO 'yyyy-mm-dd', 'w' for
// the working date, and the compact MMDDYY form — so the old throw was refusing writes BC makes.
// The measurements are in the PR that landed this and in issue #3384's table.
//
// These are claims about OUR OWN conversion helper, not about Business Central. The BC claim is
// upstream, in StefanMaron/BusinessCentral.AL.Language.Tests, where a service tier adjudicates
// it (corpus PR #263, codeunit 60670, 11/11 green on BC 28.4 before this fix was written).
//
// What is asserted here is the first of the helper's two steps, the one that needs no BC engine:
// inverting the runner's own ValueToString spelling for each of the three temporal types. That
// step is what a typed AL argument — SetValue(<Date>) — depends on, because BC's own ALSetValue
// renders it to text through ITestField.ValueToString before the control ever sees it. Step two
// hands the string to BC's own NavValueEvaluator and is exercised by the corpus tests.
//
// RED/GREEN: before the fix, LiveNavTestField.Write had no temporal arm at all and
// PageVariableTestField.ToBoundValue had only NavDate, so a Time argument was validated as the
// NavText "01/02/0001 14:30:00". Deleting the round-trip branch from TryResolve makes every
// positive assertion below fail, in milliseconds, without the BC engine loaded.
using System;
using AlRunner;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageTemporalValueTests
{
    // The exact spelling Convert.ToString(<DateTime>, InvariantCulture) produces — .NET's
    // general date/time pattern, "MM/dd/yyyy HH:mm:ss" — is what ValueToString hands over.
    [Fact]
    public void TryResolve_RoundTripSpelling_ForADateControl_AnswersThatDate()
    {
        Assert.True(TestPageTemporalValue.TryResolve(NavType.Date, "12/31/2026 00:00:00", out var v));

        var date = Assert.IsType<NavDate>(v);
        Assert.Equal(new DateTime(2026, 12, 31), date.Value.Date);
    }

    [Fact]
    public void TryResolve_RoundTripSpelling_ForADifferentDate_AnswersThatDate()
    {
        Assert.True(TestPageTemporalValue.TryResolve(NavType.Date, "01/15/2020 00:00:00", out var v));

        var date = Assert.IsType<NavDate>(v);
        Assert.Equal(new DateTime(2020, 1, 15), date.Value.Date);
    }

    // A DateTime keeps its time of day, where the Date arm above discards it.
    [Fact]
    public void TryResolve_RoundTripSpelling_ForADateTimeControl_KeepsTheTimeOfDay()
    {
        Assert.True(TestPageTemporalValue.TryResolve(
            NavType.DateTime, "01/15/2026 14:30:00", out var v));

        var dateTime = Assert.IsType<NavDateTime>(v);
        Assert.Equal(new DateTime(2026, 1, 15), dateTime.Value.Date);
        Assert.Equal(new TimeSpan(14, 30, 0), dateTime.Value.TimeOfDay);
    }

    // A Time argument arrives carrying NavTime's own base date (01/02/0001), which is the
    // spelling that produced "The value \"01/02/0001 14:30:00\" can't be evaluated into type
    // Time". Only the time of day is the value; the date part is BC's carrier and is discarded.
    [Fact]
    public void TryResolve_RoundTripSpelling_ForATimeControl_AnswersThatTimeOfDay()
    {
        Assert.True(TestPageTemporalValue.TryResolve(NavType.Time, "01/02/0001 14:30:00", out var v));

        var time = Assert.IsType<NavTime>(v);
        Assert.Equal(new TimeSpan(14, 30, 0), time.Value.TimeOfDay);
    }

    // A non-temporal control is not this helper's business: answering true for one would replace
    // whatever conversion that control's own arm performs.
    [Theory]
    [InlineData(NavType.Text)]
    [InlineData(NavType.Code)]
    [InlineData(NavType.Integer)]
    [InlineData(NavType.Boolean)]
    public void TryResolve_ANonTemporalControl_DeclinesAndProducesNothing(NavType type)
    {
        Assert.False(TestPageTemporalValue.TryResolve(type, "12/31/2026 00:00:00", out var v));
        Assert.Null(v);
    }

    // Declining is the whole contract for a spelling nothing can read: the caller then keeps its
    // NavText path, where BC raises its own refusal naming the value. Answering true with a
    // made-up default here would write a wrong date and report success — the failure mode
    // .claude/rules/loud-failures.md exists to prevent.
    [Theory]
    [InlineData("not-a-date")]
    [InlineData("@@@")]
    public void TryResolve_AnUnreadableSpelling_DeclinesRatherThanGuessing(string input)
    {
        Assert.False(TestPageTemporalValue.TryResolve(NavType.Date, input, out var v));
        Assert.Null(v);
    }

    // ---- The lenient-parse guard (PR #3394 review item 3) --------------------------------
    //
    // Step one must invert exactly the one spelling this runner emits and nothing else, so that
    // every spelling a *user* can type reaches BC's own evaluator instead. These four are the
    // discriminating ones, all measured on a real BC 28.4.53241.0 tier through a Date control:
    //
    //   'w'         accepted -> the working date        011528     accepted -> 2028-01-15
    //   't'         refused                             15.01.28   refused
    //
    // No general-purpose date parser produces those answers: .NET cannot read 'w' at all, and a
    // lenient invariant parse WOULD read '15.01.28' and '011528' — and would then answer here,
    // before BC ever saw them, substituting .NET's reading of a typed date for the platform's.
    // Asserting at the step-one seam rather than through TryResolve keeps this independent of
    // whether a BC engine is loaded in the test process.
    [Theory]
    [InlineData("w")]
    [InlineData("t")]
    [InlineData("011528")]
    [InlineData("15.01.28")]
    [InlineData("2026-01-15")]
    [InlineData("01/15/26")]
    public void TryResolveRoundTrip_ASpellingAUserCanType_IsLeftToBcsOwnEvaluator(string typed)
    {
        Assert.False(TestPageTemporalValue.TryResolveRoundTrip(NavType.Date, typed, out var v));
        Assert.Null(v);
    }

    // The paired positive, so the theory above cannot pass by the step declining everything.
    [Fact]
    public void TryResolveRoundTrip_TheRunnersOwnSpelling_IsTheOneItAccepts()
    {
        Assert.True(TestPageTemporalValue.TryResolveRoundTrip(
            NavType.Date, "01/15/2026 00:00:00", out var v));

        var date = Assert.IsType<NavDate>(v);
        Assert.Equal(new DateTime(2026, 1, 15), date.Value.Date);
    }

    // ---- The binding is real on the pinned Ncl (PR #3394 review item 4) -------------------
    //
    // NavValueEvaluator, NavNclType and the 6-parameter Evaluate are internal to Ncl.dll, so
    // step two binds them by reflection off a shape read from the 28.1/28.4 decompiles. If a BC
    // build moves that shape the binding throws rather than declining — but a throw nobody
    // provokes proves nothing, so this provokes it against the Ncl this build is pinned to.
    //
    // Touching NavDate first is not incidental: the assembly is loaded lazily, and asserting
    // before anything has referenced a Ncl type would measure the load order, not the binding.
    [Fact]
    public void EnsureEvaluatorBound_BindsAgainstThePinnedNcl_RatherThanThrowing()
    {
        _ = NavDate.Create(DateTime.SpecifyKind(new DateTime(2026, 1, 15), DateTimeKind.Local));
        Assert.Contains(
            AppDomain.CurrentDomain.GetAssemblies(),
            a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");

        // Throws RunnerOutOfScopeException, naming testpage-temporal-evaluator, if the shape has
        // moved. Passing means the reflection above matches the Ncl this build actually loads.
        TestPageTemporalValue.EnsureEvaluatorBound();
    }
}
