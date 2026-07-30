using System;
using System.Reflection;
using System.Threading.Tasks;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// AL subscribers are not always emitted as synchronous methods — BC's own compiler
/// emits an async state machine whenever the body needs one (e.g. the System App's
/// ReportManagement.OnCustomDocumentMergerEx, which raises a further event). An
/// exception raised inside such a body is captured by the state machine and surfaced
/// on the RETURNED TASK; it does not propagate out of MethodInfo.Invoke.
///
/// The dispatcher used to discard that return value, so the error vanished and the
/// publisher continued as if the subscriber had succeeded. Measured consequence: an
/// ISV report renderer's "LF-XML: The template is not well-formed XML" was thrown and
/// dropped, the platform handed the caller an EMPTY document, Report.SaveAs reported
/// success, and the AL test failed later with a misleading "Not a native PDF".
///
/// NOTE ON COVERAGE: this cannot be pinned from first-party AL — the runner's own
/// compile pipeline emits these subscribers synchronously, so an AL-level reproducer
/// passes with and without the fix (verified). The async shape comes from
/// MS-precompiled AL, so the contract is pinned here at the seam instead.
/// </summary>
public class DispatchObserveAsyncResultTests
{
    private static MethodInfo AnyMethod =>
        typeof(DispatchObserveAsyncResultTests).GetMethod(nameof(AnyMethodTarget),
            BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void AnyMethodTarget() { }

    [Fact]
    public void FaultedTask_RethrowsTheOriginalException()
    {
        var failed = Task.FromException(new InvalidOperationException("LF-XML: template not well-formed"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => BcRuntime.ObserveAsyncResult(failed, AnyMethod));

        // The ORIGINAL exception, not an AggregateException — AL callers must see their own error.
        Assert.Equal("LF-XML: template not well-formed", ex.Message);
    }

    [Fact]
    public void FaultedValueTask_RethrowsTheOriginalException()
    {
        var failed = new ValueTask(Task.FromException(new InvalidOperationException("boom")));

        var ex = Assert.Throws<InvalidOperationException>(
            () => BcRuntime.ObserveAsyncResult(failed, AnyMethod));

        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public void FaultedGenericValueTask_RethrowsTheOriginalException()
    {
        var failed = new ValueTask<int>(Task.FromException<int>(new InvalidOperationException("generic boom")));

        var ex = Assert.Throws<InvalidOperationException>(
            () => BcRuntime.ObserveAsyncResult(failed, AnyMethod));

        Assert.Equal("generic boom", ex.Message);
    }

    [Fact]
    public void SuccessfulResults_AreObservedWithoutThrowing()
    {
        // A synchronous subscriber returns null; a completed async one returns a completed
        // task. Neither may be turned into a failure by the observation itself.
        BcRuntime.ObserveAsyncResult(null, AnyMethod);
        BcRuntime.ObserveAsyncResult(Task.CompletedTask, AnyMethod);
        BcRuntime.ObserveAsyncResult(default(ValueTask), AnyMethod);
        BcRuntime.ObserveAsyncResult(new ValueTask<int>(7), AnyMethod);
    }

    [Fact]
    public void NonTaskReturnValue_IsIgnored()
    {
        // AL subscribers may legitimately return a plain value; observing must not choke.
        BcRuntime.ObserveAsyncResult("a string", AnyMethod);
        BcRuntime.ObserveAsyncResult(42, AnyMethod);
    }
}
