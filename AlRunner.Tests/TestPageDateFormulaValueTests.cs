// TestPageDateFormulaValueTests — contract tests for TestPageTemporalValue.TryResolveDateFormula
// (issue #3501).
//
// These are claims about the runner's OWN routing, not about Business Central. The BC claim —
// what a DateFormula-typed TestPage control does with the value SetValue hands it — is upstream
// in StefanMaron/BusinessCentral.AL.Language.Tests, codeunit 60601, where a service tier
// adjudicates it across both bindings and both value shapes.
//
// What is pinned here is the routing decision that has no BC-behaviour content: which NavType
// reaches the DateFormula path and which does not. That matters because the fix has two halves
// in two files — PageVariableTestField.FieldType must answer NavType.DateFormula for a
// NavDateFormula binding, and both write paths must call TryResolveDateFormula — and a routing
// mistake in either half is invisible to a test that only checks the resulting value.
//
// RED/GREEN: before the fix there was no DateFormula arm at all. A NavText fell through to the
// binding and the two paths failed differently — the page-variable path raised
// InvalidCastException ('NavText' to 'NavDateFormula'), while the Rec-bound path stored a
// TRUNCATED value and raised nothing, leaving '<1D>' as '<'. The silent half is why the
// declining direction below is asserted rather than assumed.
using AlRunner;
using Microsoft.Dynamics.Nav.Types;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageDateFormulaValueTests
{
    // ---- The declining direction ---------------------------------------------------------
    //
    // TryResolveDateFormula must answer false for every type that is NOT DateFormula, because
    // the callers chain it ahead of their other arms: a helper that claimed a Text or an
    // Integer control would divert those writes into DateFormula evaluation and silently
    // change what an unrelated control stores. The two corpus Text-control arms are the
    // AL-observable half of this same claim.

    [Theory]
    [InlineData(NavType.Text)]
    [InlineData(NavType.Code)]
    [InlineData(NavType.Integer)]
    [InlineData(NavType.Decimal)]
    [InlineData(NavType.Boolean)]
    [InlineData(NavType.Option)]
    [InlineData(NavType.Date)]
    [InlineData(NavType.DateTime)]
    [InlineData(NavType.Time)]
    public void TryResolveDateFormula_ForANonDateFormulaType_Declines(NavType type)
    {
        // '<1D>' is deliberately a value the DateFormula evaluator WOULD accept, so the arm
        // can only decline because of the type — not because the string was unreadable.
        Assert.False(TestPageTemporalValue.TryResolveDateFormula(type, "<1D>", out var resolved));
        Assert.Null(resolved);
    }

    // The mirror of the theory above, and the reason it is a separate test: a helper that
    // declined EVERYTHING would satisfy every row above while doing nothing at all. This is
    // the control.
    [Fact]
    public void TryResolveDateFormula_ForADateFormulaControl_DoesNotDecline()
    {
        Assert.True(TestPageTemporalValue.TryResolveDateFormula(NavType.DateFormula, "<1D>", out var resolved));
        Assert.NotNull(resolved);
    }

    // ---- The two arms are disjoint -------------------------------------------------------
    //
    // TryResolve (the Date/DateTime/Time helper) and TryResolveDateFormula are chained one
    // after the other in both write paths, so each must decline what the other claims. If
    // TryResolve ever started answering for DateFormula the chain order would decide the
    // result, which is exactly the kind of dependency that survives every value assertion.
    [Fact]
    public void TryResolve_ForADateFormulaControl_Declines()
    {
        Assert.False(TestPageTemporalValue.TryResolve(NavType.DateFormula, "<1D>", out var resolved));
        Assert.Null(resolved);
    }

    // NavType.DateFormula must actually exist on the Types.dll this build is pinned to. The
    // routing above is keyed on that member, so a BC build that renamed it would leave every
    // assertion here passing against a constant the runtime no longer recognises.
    [Fact]
    public void NavType_DeclaresDateFormula_OnThePinnedTypesAssembly()
    {
        Assert.True(System.Enum.IsDefined(typeof(NavType), NavType.DateFormula));
    }
}
