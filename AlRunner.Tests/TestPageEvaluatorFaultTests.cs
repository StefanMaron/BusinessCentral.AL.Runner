// TestPageEvaluatorFaultTests - a fault on the BC-evaluator path is named, not reported as
// BC refusing the value (issue #3444).
//
// TestPageTemporalValue asks BC's own NavValueEvaluator how this build reads a date, time or
// datetime a test typed as text. It asks under DataError.TrapError, and the 28.1 Ncl decompile
// of NavValueEvaluator`1.Evaluate settles what that mode means: a refusal returns false, and
// the one refusal type raised from inside - NavNCLEvaluateException - is caught by BC itself
// when dataError == 0, which DataError.TrapError is. So an exception escaping Evaluate is the
// evaluator not completing, and answering false for it sends the caller back to the NavText
// path where the AL author reads BC's date-format refusal for a value BC would have accepted.
//
// RED against main: every arm below that expects BcShapeGapException got `false` instead,
// because the two catches at the call site returned false for any exception at all.
using System;
using System.Reflection;
using AlRunner;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Microsoft.Dynamics.Nav.Types.Exceptions;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageEvaluatorFaultTests
{
    // Scenario 1 of the issue: unpopulated skeleton state NREs inside BC's evaluator, arrives
    // wrapped by MethodBase.Invoke, and used to be indistinguishable from "BC said no".
    [Fact]
    public void InvokeEvaluate_WhenBcsEvaluatorFaults_RaisesAShapeGapNamingTheFault()
    {
        var gap = Assert.Throws<BcShapeGapException>(() => TestPageTemporalValue.InvokeEvaluate(
            () => throw new TargetInvocationException(
                new NullReferenceException("session.RegionalSettings was null"))));

        Assert.Equal("NavValueEvaluator.Evaluate", gap.Member);
        Assert.Contains("NullReferenceException", gap.Detail);
        Assert.Contains("session.RegionalSettings was null", gap.Detail);
        Assert.Contains("did not complete", gap.Detail);
    }

    // BcShapeGapException specifically, because that is the one an AL asserterror cannot
    // absorb (NavMethodScope_AssertError rethrows only this type). A RunnerOutOfScopeException
    // here would let `asserterror SetValue(...)` pass on a runner fault.
    [Fact]
    public void InvokeEvaluate_WhenBcsEvaluatorFaults_RaisesTheTypeAssertErrorCannotSwallow()
    {
        var thrown = Record.Exception(() => TestPageTemporalValue.InvokeEvaluate(
            () => throw new TargetInvocationException(new InvalidOperationException("boom"))));

        Assert.NotNull(thrown);
        Assert.NotNull(BcShapeGapException.Find(thrown));
        Assert.IsNotType<RunnerOutOfScopeException>(thrown);
    }

    // Scenario 2: an ArgumentException arrives UNWRAPPED, so it came from Invoke rather than
    // from BC - the bound Evaluate does not take the arguments this call site passes.
    [Fact]
    public void InvokeEvaluate_WhenTheBoundEvaluateRefusesTheArguments_RaisesAVersionMismatch()
    {
        var gap = Assert.Throws<BcShapeGapException>(() => TestPageTemporalValue.InvokeEvaluate(
            () => throw new ArgumentException("parameter count mismatch")));

        Assert.Equal("NavValueEvaluator.Evaluate", gap.Member);
        Assert.Contains("parameter count mismatch", gap.Detail);
        Assert.Contains("version mismatch", gap.Detail);
    }

    // The one exception shape that IS a refusal. It cannot escape Evaluate under TrapError on
    // the pinned build, but if a build raises it past the filter the answer is still "BC said
    // no", and declining leaves BC's own message as the observable outcome.
    [Fact]
    public void InvokeEvaluate_WhenBcRaisesItsOwnEvaluateRefusal_DeclinesWithoutThrowing()
    {
        Assert.False(TestPageTemporalValue.InvokeEvaluate(
            () => throw new TargetInvocationException(
                new NavNCLEvaluateException("cannot be evaluated into type Date"))));
    }

    [Fact]
    public void InvokeEvaluate_WhenTheEvaluatorAnswers_PassesThatAnswerThrough()
    {
        Assert.True(TestPageTemporalValue.InvokeEvaluate(() => true));
        Assert.False(TestPageTemporalValue.InvokeEvaluate(() => false));
    }

    // The control the issue asked for: a genuine decline still reaches the NavText path.
    //
    // With the fix in place this is no longer ambiguous. Before it, a refusal and a fault both
    // produced false here, so the assertion held either way and no reader could tell which had
    // happened. Measured on this build while writing the fix: the session IS populated in the
    // xunit process and BC's evaluator refuses '@@@' by RETURNING false, so nothing throws -
    // which is why the assertion below and TestPageTemporalValueTests's own
    // TryResolve_AnUnreadableSpelling_DeclinesRatherThanGuessing are now real controls.
    [Fact]
    public void TryResolve_AnUnreadableSpelling_IsRefusedByBcRatherThanFaulting()
    {
        _ = NavDate.Create(DateTime.SpecifyKind(new DateTime(2026, 1, 15), DateTimeKind.Local));
        Assert.NotNull(BcRuntime.SkeletonSession);

        Assert.False(TestPageTemporalValue.TryResolve(NavType.Date, "@@@", out var resolved));
        Assert.Null(resolved);
    }
}
