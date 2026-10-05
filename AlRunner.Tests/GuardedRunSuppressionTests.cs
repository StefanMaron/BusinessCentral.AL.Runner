// GuardedRunSuppressionTests — which exceptions a guarded `Codeunit.Run` turns into `false` (#5342).
//
// BC's NavCodeunit.DoRunAsync wraps OnRun, on its guarded branch (`errorLevel != ThrowError`, the
// Boolean result consumed), in `catch (NavBaseException)` and nothing else (Ncl 28.4.53241.54346,
// 6f2cf682…; the body is identical on 27.5.46862.53931, affa03c9…). The runner's two replacements,
// NavCodeunit_DoRunAsync (instance form) and NavCodeunit_RunCodeunit (static form), caught
// everything, so a refusal became `ok=No lastError=[]`. Both now ask BcRuntime.GuardedRunSuppresses:
// an AL error is suppressed, and so is a PERMANENT out-of-scope refusal, as a [TryFunction] traps it
// (ApplicationObjectBasePatches.IsPermanentOutOfScope, one classification), loudly. Every other runner
// refusal escapes.
//
// The AL-level proof, in both spellings, is tests/runner-extras/guarded-codeunit-run-refusal; the BC
// half (a guarded run returns false and keeps the inner error text) is corpus codeunit 60217. What
// only C# can pin is the classification of exception SHAPES the AL cannot provoke from a
// source-compiled test: a refusal wrapped inside a BC AL error, a refusal arriving as the
// "out-of-scope: " message convention, a raw CLR exception (a test reaching one always fails on BC, so
// the corpus cannot express it), and the stderr line itself, which AL cannot read.

using System;
using System.IO;
using System.Reflection;
using AlRunner;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Types.Exceptions;
using Xunit;

namespace AlRunner.Tests;

// Serial: the stderr line is read by swapping Console.Error, which is process-wide.
[Collection(ConsoleFilterSerialCollection.Name)]
public sealed class GuardedRunSuppressionTests
{
    private static RunnerOutOfScopeException Permanent(string api = "TaskScheduler.TaskExists") =>
        new(api, "task-scheduler", "jobs");

    private static RunnerOutOfScopeException NotYetImplemented(string api = "SQL connection") =>
        new(api, "not-yet-implemented — probe", "docs/limitations.md");

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

    // ── A PERMANENT out-of-scope refusal is trapped, as a [TryFunction] traps it ──

    [Fact]
    public void ATypedPermanentRefusal_IsSuppressed()
    {
        Assert.True(BcRuntime.GuardedRunSuppresses(Permanent()));
    }

    [Fact]
    public void APermanentRefusal_WrappedInAnAlError_OrAReflectionWrapper_IsStillSuppressed()
    {
        // The chain walk: BC's own remap can hand a refusal back inside a NavBaseException, and
        // MethodBase.Invoke wraps one in a TargetInvocationException.
        Assert.True(BcRuntime.GuardedRunSuppresses(new NavALException("an AL error", Permanent())));
        Assert.True(BcRuntime.GuardedRunSuppresses(new TargetInvocationException(Permanent())));
    }

    // ── Everything else a runner refusal can be escapes ──

    [Fact]
    public void ANotYetImplementedRefusal_Escapes_BareOrWrapped()
    {
        Assert.False(BcRuntime.GuardedRunSuppresses(NotYetImplemented()));
        Assert.False(BcRuntime.GuardedRunSuppresses(new NavALException("an AL error", NotYetImplemented())));
        Assert.False(BcRuntime.GuardedRunSuppresses(new TargetInvocationException(NotYetImplemented())));
    }

    [Fact]
    public void TheOutOfScopeMessageConvention_RaisedAsAnInvalidOperationException_Escapes()
    {
        // A Cecil-injected throw site cannot construct the typed exception (#1743). Only a TYPED
        // permanent refusal is trapped, the test NavApplicationObjectBase_TryInvoke applies.
        Assert.False(BcRuntime.GuardedRunSuppresses(new InvalidOperationException(
            "out-of-scope: NavReport.<Layout> — layout rendering requires service tier — see docs/scope.md#report-rendering")));
    }

    [Fact]
    public void ARefusalCarriedAsTheMessageOfAnAlError_Escapes()
    {
        Assert.False(BcRuntime.GuardedRunSuppresses(new NavALException(
            "out-of-scope: TaskScheduler.TaskExists — task-scheduler — see docs/scope.md#jobs")));
    }

    [Fact]
    public void AShapeGap_Escapes_BareOrWrapped_EvenBesideAPermanentRefusal()
    {
        var gap = new BcShapeGapException("NavX", "NavX.Y", "probe");

        Assert.False(BcRuntime.GuardedRunSuppresses(gap));
        Assert.False(BcRuntime.GuardedRunSuppresses(new NavALException("an AL error", gap)));
        // A shape gap on the chain beats a permanent refusal on it, the order TryInvoke uses.
        Assert.False(BcRuntime.GuardedRunSuppresses(new AggregateException(gap, Permanent())));
        Assert.False(BcRuntime.GuardedRunSuppresses(new AggregateException(Permanent(), gap)));
    }

    [Fact]
    public void ACorruptDependencyPackage_Escapes_BareOrWrapped()
    {
        var read = new BcAppSymbolReadException("/x/dep.app", "table symbols", new OutOfMemoryException("probe"));

        Assert.False(BcRuntime.GuardedRunSuppresses(read));
        Assert.False(BcRuntime.GuardedRunSuppresses(new NavALException("an AL error", read)));
    }

    [Fact]
    public void ARawClrException_Escapes()
    {
        Assert.False(BcRuntime.GuardedRunSuppresses(new NullReferenceException("probe")));
        Assert.False(BcRuntime.GuardedRunSuppresses(new InvalidOperationException("boom")));
    }

    // ── The loud half: the same [oos-in-try] line a [TryFunction] prints ──

    private static string CaptureStderr(Action body)
    {
        var original = Console.Error;
        var sink = new StringWriter();
        try
        {
            Console.SetError(sink);
            body();
        }
        finally { Console.SetError(original); }
        return sink.ToString();
    }

    [Fact]
    public void ATrappedPermanentRefusal_PrintsTheOosInTryLine_NamingTheSeam_OncePerSurface()
    {
        var api = "Probe.Surface." + Guid.NewGuid().ToString("N");
        var ex = new NavALException("an AL error", Permanent(api));

        var first = CaptureStderr(() => BcRuntime.ReportTrappedPermanentRefusal(ex, "a guarded Codeunit.Run"));
        var second = CaptureStderr(() => BcRuntime.ReportTrappedPermanentRefusal(ex, "a guarded Codeunit.Run"));
        var otherSeam = CaptureStderr(() => BcRuntime.ReportTrappedPermanentRefusal(ex, "a guarded StartSession"));

        Assert.Contains($"[oos-in-try] {api} — task-scheduler", first, StringComparison.Ordinal);
        Assert.Contains("reached inside a guarded Codeunit.Run; returning false", first, StringComparison.Ordinal);
        Assert.Equal("", second);
        Assert.Contains("reached inside a guarded StartSession; returning false", otherSeam, StringComparison.Ordinal);
    }

    [Fact]
    public void ATrappedAlError_OrAnEscapingRefusal_PrintsNothing()
    {
        var text = CaptureStderr(() =>
        {
            BcRuntime.ReportTrappedPermanentRefusal(new NavALException("an AL error"), "a guarded Codeunit.Run");
            BcRuntime.ReportTrappedPermanentRefusal(NotYetImplemented("Probe.Nyi." + Guid.NewGuid().ToString("N")),
                "a guarded Codeunit.Run");
        });

        Assert.Equal("", text);
    }

    // ── The same classification, as StartSession's TrapError catch asks it (#5342) ──

    [Fact]
    public void IsRunnerRefusal_SeesARefusalBehindTheReflectionWrapper()
    {
        // trigger.Invoke wraps whatever the worker raises in a TargetInvocationException.
        Assert.True(BcRuntime.IsRunnerRefusal(new TargetInvocationException(Permanent())));
        Assert.True(BcRuntime.IsRunnerRefusal(new TargetInvocationException(NotYetImplemented())));
    }

    [Fact]
    public void IsRunnerRefusal_IsNotTrueOfAnOrdinaryWorkerError()
    {
        Assert.False(BcRuntime.IsRunnerRefusal(new TargetInvocationException(new NavALException("an AL error"))));
        Assert.False(BcRuntime.IsRunnerRefusal(new NullReferenceException("probe")));
    }

    [Fact]
    public void StartSessionTraps_OnlyAPermanentRefusal_AmongRefusals()
    {
        static bool Traps(Exception e) => BcRuntime.StartSessionTrapSuppresses(e);

        Assert.True(Traps(new TargetInvocationException(Permanent())));
        Assert.False(Traps(new TargetInvocationException(NotYetImplemented())));
        Assert.False(Traps(new TargetInvocationException(new BcShapeGapException("NavX", "NavX.Y", "probe"))));
        Assert.True(Traps(new NullReferenceException("a worker bug, unchanged")));
    }
}
