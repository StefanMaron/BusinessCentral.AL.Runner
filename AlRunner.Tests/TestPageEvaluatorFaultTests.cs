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
using System.Collections.Generic;
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
            () => throw new ArgumentException("the argument is of the wrong type")));

        Assert.Equal("NavValueEvaluator.Evaluate", gap.Member);
        Assert.Contains("the argument is of the wrong type", gap.Detail);
        Assert.Contains("version mismatch", gap.Detail);
    }

    // The two shapes MethodBase.Invoke raises UNWRAPPED that the ArgumentException arm above
    // does not cover: both derive from ApplicationException, not from ArgumentException, so on
    // main they propagated raw past every arm (#3462). RED against main: BcShapeGapException
    // was not thrown - the raw exception came out instead.
    public static IEnumerable<object[]> UnwrappedInvokeFaults() => new[]
    {
        new object[] { new TargetParameterCountException("Parameter count mismatch.") },
        new object[] { new TargetException("Object does not match target type.") },
    };

    [Theory]
    [MemberData(nameof(UnwrappedInvokeFaults))]
    public void InvokeEvaluate_WhenInvokeItselfRefusesTheCall_RaisesAVersionMismatch(Exception injected)
    {
        var gap = Assert.Throws<BcShapeGapException>(
            () => TestPageTemporalValue.InvokeEvaluate(() => throw injected));

        Assert.Equal("NavValueEvaluator.Evaluate", gap.Member);
        Assert.Contains(injected.GetType().Name, gap.Detail);
        Assert.Contains("version mismatch", gap.Detail);
    }

    // ...and the consequence, on the seam that INVERTS a result. An asserterror around a
    // SetValue that hits one of these must still fail; before the fix NavMethodScope_AssertError
    // absorbed the raw exception on its catch-all and the asserterror PASSED.
    [Theory]
    [MemberData(nameof(UnwrappedInvokeFaults))]
    public void InvokeEvaluate_WhenInvokeItselfRefusesTheCall_IsNotSwallowedByAssertError(Exception injected)
    {
        var gap = Assert.Throws<BcShapeGapException>(() => BcRuntime.NavMethodScope_AssertError(
            null!, () => TestPageTemporalValue.InvokeEvaluate(() => throw injected)));

        Assert.Equal("NavValueEvaluator.Evaluate", gap.Member);
    }

    // ══ The bind path two frames up (#3462) ══════════════════════════════════════════════
    //
    // A bind failure means the runner could not ask THIS BC build how it reads a date at all -
    // a runner/BC-version mismatch, which BcShapeGapException is the type for. It refused with
    // RunnerOutOfScopeException, which an asserterror absorbs, so `asserterror SetValue(...)`
    // on a temporal control passed green having measured nothing.
    //
    // The bind result latches once per process, so these drive the refusal through the
    // ForceBindFailure seam rather than by breaking the real binding for the rest of the run.

    [Fact]
    public void EnsureEvaluatorBound_WhenTheBindFailed_RaisesAShapeGapNamingTheReason()
    {
        using (TestPageTemporalValue.ForceBindFailure("NavValueEvaluator not found"))
        {
            var gap = Assert.Throws<BcShapeGapException>(TestPageTemporalValue.EnsureEvaluatorBound);

            Assert.Equal("NavValueEvaluator.Evaluate", gap.Member);
            Assert.Contains("NavValueEvaluator not found", gap.Detail);
            Assert.Contains("version mismatch", gap.Detail);
        }
    }

    [Fact]
    public void EnsureEvaluatorBound_WhenTheBindFailed_IsNotSwallowedByAssertError()
    {
        // RED against main: NavMethodScope_AssertError rethrows only BcShapeGapException and
        // absorbed the RunnerOutOfScopeException on its catch-all, so this returned normally
        // and the AL asserterror it stands for passed on a runner fault.
        using (TestPageTemporalValue.ForceBindFailure("NavValueEvaluator.Evaluate not found"))
        {
            var gap = Assert.Throws<BcShapeGapException>(() => BcRuntime.NavMethodScope_AssertError(
                null!, TestPageTemporalValue.EnsureEvaluatorBound));

            Assert.Equal("TestPage SetValue on a Date/DateTime/Time control", gap.Surface);
        }
    }

    [Fact]
    public void EnsureEvaluatorBound_WhenTheBindFailed_IsNotAbsorbableAsAnOutOfScopeSignal()
    {
        // The other half: anything carrying a RunnerOutOfScopeException can be declared away by
        // an `expect-oos` manifest entry, and which BC build is on disk must never be
        // declarable as an expected scope boundary.
        using (TestPageTemporalValue.ForceBindFailure("DataError.TrapError not found"))
        {
            var thrown = Record.Exception(TestPageTemporalValue.EnsureEvaluatorBound);

            Assert.NotNull(thrown);
            Assert.IsNotType<RunnerOutOfScopeException>(thrown);
            Assert.Null(OutOfScopeMessage.FromException(thrown));
        }
    }

    // The seam is a seam, not a switch: with nothing forced, the real bind still decides. On
    // this build it succeeds, so EnsureEvaluatorBound returns - which is also what makes the
    // three arms above statements about the refusal rather than about the seam.
    [Fact]
    public void EnsureEvaluatorBound_WithNothingForced_StillBindsThisBuildsEvaluator()
    {
        using (TestPageTemporalValue.ForceBindFailure("probe")) { }

        TestPageTemporalValue.EnsureEvaluatorBound();
        Assert.True(TestPageTemporalValue.TryResolve(
            NavType.Date, "01/15/2026 00:00:00", out var resolved));
        Assert.NotNull(resolved);
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
