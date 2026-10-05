// GuardedRunSuppressionTests — which exceptions a guarded `Codeunit.Run` turns into `false` (#5342).
//
// BC's NavCodeunit.DoRunAsync wraps OnRun, on its guarded branch (`errorLevel != ThrowError`, the
// Boolean result consumed), in `catch (NavBaseException)` and nothing else (Ncl 28.4.53241.54346,
// 6f2cf682…; the body is identical on 27.5.46862.53931, affa03c9…). The runner's two replacements,
// NavCodeunit_DoRunAsync (instance form) and NavCodeunit_RunCodeunit (static form), caught
// everything, so a refusal became `ok=No lastError=[]`. Both now ask BcRuntime.GuardedRunSuppresses.
//
// The AL-level proof, in both spellings, is tests/runner-extras/guarded-codeunit-run-refusal;
// the BC half (a guarded run returns false and keeps the inner error text) is corpus codeunit
// 60217. What only C# can pin is the classification of exception SHAPES the AL cannot provoke
// from a source-compiled test: a refusal wrapped inside a BC AL error, a refusal arriving as the
// "out-of-scope: " message convention, and a raw CLR exception — which a test reaching it always
// fails on BC, so the corpus cannot express it.

using System;
using System.Reflection;
using AlRunner;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Types.Exceptions;
using Xunit;

namespace AlRunner.Tests;

public sealed class GuardedRunSuppressionTests
{
    // ── What BC suppresses ──

    [Fact]
    public void AnAlError_IsSuppressed()
    {
        Assert.True(BcRuntime.GuardedRunSuppresses(new NavALException("an AL error")));
    }

    [Fact]
    public void ANavBaseExceptionBcRaisesItself_IsSuppressed()
    {
        // The runner's own stand-ins for AL errors are NavCSideException (a NavBaseException):
        // the shape every "does not exist" / TestField error takes.
        var built = Activator.CreateInstance(
            typeof(NavCSideException), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new object[] { "a BC error" }, null) as Exception;

        Assert.IsAssignableFrom<NavBaseException>(built);
        Assert.True(BcRuntime.GuardedRunSuppresses(built!));
    }

    // ── What BC does not suppress, and a runner refusal never is ──

    [Fact]
    public void ARawClrException_Escapes()
    {
        Assert.False(BcRuntime.GuardedRunSuppresses(new NullReferenceException("probe")));
        Assert.False(BcRuntime.GuardedRunSuppresses(new InvalidOperationException("boom")));
    }

    [Fact]
    public void ATypedOutOfScopeRefusal_Escapes_OfEitherFlavour()
    {
        Assert.False(BcRuntime.GuardedRunSuppresses(
            new RunnerOutOfScopeException("TaskScheduler.TaskExists", "task-scheduler", "jobs")));
        Assert.False(BcRuntime.GuardedRunSuppresses(
            new RunnerOutOfScopeException("SQL connection", "not-yet-implemented — probe", "docs/limitations.md")));
    }

    [Fact]
    public void TheOutOfScopeMessageConvention_RaisedAsAnInvalidOperationException_Escapes()
    {
        // A Cecil-injected throw site cannot construct the typed exception (#1743).
        Assert.False(BcRuntime.GuardedRunSuppresses(new InvalidOperationException(
            "out-of-scope: NavReport.<Layout> — layout rendering requires service tier — see docs/scope.md#report-rendering")));
    }

    [Fact]
    public void ARefusalWrappedInAnAlError_StillEscapes()
    {
        // BC's own RemapToALExceptionAndThrow can hand a refusal back inside a NavBaseException,
        // which the type test alone would suppress.
        var wrapped = new NavALException("an AL error",
            new RunnerOutOfScopeException("TaskScheduler.TaskExists", "task-scheduler", "jobs"));

        Assert.IsAssignableFrom<NavBaseException>(wrapped);
        Assert.False(BcRuntime.GuardedRunSuppresses(wrapped));
    }

    [Fact]
    public void ARefusalCarriedAsTheMessageOfAnAlError_StillEscapes()
    {
        Assert.False(BcRuntime.GuardedRunSuppresses(new NavALException(
            "out-of-scope: TaskScheduler.TaskExists — task-scheduler — see docs/scope.md#jobs")));
    }

    [Fact]
    public void AShapeGap_Escapes_BareOrWrapped()
    {
        var gap = new BcShapeGapException("NavX", "NavX.Y", "probe");

        Assert.False(BcRuntime.GuardedRunSuppresses(gap));
        Assert.False(BcRuntime.GuardedRunSuppresses(new NavALException("an AL error", gap)));
    }

    [Fact]
    public void ACorruptDependencyPackage_Escapes_BareOrWrapped()
    {
        var read = new BcAppSymbolReadException("/x/dep.app", "table symbols", new OutOfMemoryException("probe"));

        Assert.False(BcRuntime.GuardedRunSuppresses(read));
        Assert.False(BcRuntime.GuardedRunSuppresses(new NavALException("an AL error", read)));
    }

    // ── The same classification, as StartSession's TrapError catch asks it (#5342) ──

    [Fact]
    public void IsRunnerRefusal_SeesARefusalBehindTheReflectionWrapper()
    {
        // trigger.Invoke wraps whatever the worker raises in a TargetInvocationException.
        var wrapped = new TargetInvocationException(
            new RunnerOutOfScopeException("TaskScheduler.TaskExists", "task-scheduler", "jobs"));

        Assert.True(BcRuntime.IsRunnerRefusal(wrapped));
    }

    [Fact]
    public void IsRunnerRefusal_IsNotTrueOfAnOrdinaryWorkerError()
    {
        Assert.False(BcRuntime.IsRunnerRefusal(new TargetInvocationException(new NavALException("an AL error"))));
        Assert.False(BcRuntime.IsRunnerRefusal(new NullReferenceException("probe")));
    }
}
