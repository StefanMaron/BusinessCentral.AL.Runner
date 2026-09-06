using System;
using System.Threading.Tasks;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Pins <c>BcRuntime.AwaitIfTask</c>, the third of the runner's three async-unwrapping seams
/// and — until #3115 — the only one with no tests at all.
///
/// The other two are <c>BcRuntime.ObserveAsyncResult</c> (event dispatch;
/// DispatchObserveAsyncResultTests.cs and ObservingSubscriberMethodInfoTests.cs) and
/// <c>RunnerPageInstance.AwaitTriggerResult</c> (page triggers; PageTriggerAwaitTests.cs). All
/// three exist for one reason: BC's AL compiler emits an async state machine whenever the body
/// needs one, an exception raised inside such a body is CAPTURED onto the returned
/// Task/ValueTask instead of thrown, and discarding that awaitable therefore discards the AL
/// author's Error().
///
/// This seam is the one on <c>Codeunit.Run</c> (CodeunitPatches) and on the report trigger
/// invocations in NavReportSync, so a swallow here means a codeunit or report that raised an
/// error appears to have succeeded — see .claude/rules/loud-failures.md.
///
/// WHY C# AND NOT AL: the shape being covered is which CLR arm unwraps which awaitable type.
/// AL cannot observe or distinguish that — an AL reproducer takes whichever arm the emit
/// produced and passes either way — and the runner's own emitter produces synchronous bodies,
/// so first-party AL cannot even reach the async shapes. The BC-behaviour claim that an AL
/// error reaches its caller is pinned upstream in the al-language corpus; what is pinned here
/// is the runner-side mechanism that makes it hold.
/// </summary>
public class AwaitIfTaskTests
{
    [Fact]
    public void FaultedTask_RethrowsTheOriginalException()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BcRuntime.AwaitIfTask(Task.FromException(new InvalidOperationException("task boom"))));

        // The ORIGINAL exception, not an AggregateException — an AL `asserterror` matches on
        // the message, so a wrapper would break it.
        Assert.Equal("task boom", ex.Message);
    }

    [Fact]
    public void FaultedValueTask_RethrowsTheOriginalException()
    {
        var failed = new ValueTask(Task.FromException(new InvalidOperationException("valuetask boom")));

        var ex = Assert.Throws<InvalidOperationException>(() => BcRuntime.AwaitIfTask(failed));

        Assert.Equal("valuetask boom", ex.Message);
    }

    [Fact]
    public void FaultedGenericValueTask_RethrowsTheOriginalException_ThroughTheReflectionArm()
    {
        // ValueTask<T> is the arm that needs reflection: boxed, it matches neither
        // `case Task t` nor `case ValueTask vt`, so AwaitIfTask can only reach it through
        // ValueTask<>.AsTask. Assert that first, so this test cannot quietly drift onto one of
        // the arms above and stop proving anything about the reflection. Remove that arm and
        // this test fails with "no exception was thrown" — the silent swallow itself.
        object failed = new ValueTask<int>(Task.FromException<int>(new InvalidOperationException("generic boom")));
        Assert.IsType<ValueTask<int>>(failed);
        Assert.False(failed is Task, "ValueTask<T> must not match `case Task t`");
        Assert.False(failed is ValueTask, "ValueTask<T> must not match `case ValueTask vt`");

        var ex = Assert.Throws<InvalidOperationException>(() => BcRuntime.AwaitIfTask(failed));

        Assert.Equal("generic boom", ex.Message);
    }

    [Fact]
    public void SuccessfulResults_AreAwaitedWithoutBeingTurnedIntoFailures()
    {
        // Negative half: a synchronous trigger returns null, a completed async one returns a
        // completed awaitable, and neither may be turned into a failure by the observation.
        BcRuntime.AwaitIfTask(null);
        BcRuntime.AwaitIfTask(Task.CompletedTask);
        BcRuntime.AwaitIfTask(default(ValueTask));
        BcRuntime.AwaitIfTask(new ValueTask<int>(7));
    }

    [Fact]
    public void NonAwaitableReturnValue_IsIgnored()
    {
        // A trigger may legitimately return an ordinary value; unwrapping must not choke on it.
        BcRuntime.AwaitIfTask("a string");
        BcRuntime.AwaitIfTask(42);
    }

    [Fact]
    public void AwaitedBodyRunsToCompletion_NotJustToItsFirstAwait()
    {
        // "It did not throw" is not enough: the state machine has to have RUN. An
        // AwaitIfTask that returned before completing the awaitable would pass every test
        // above that only checks for the absence of an exception.
        int ran = 0;
        async ValueTask<int> Body()
        {
            ran++;
            await Task.Yield();
            ran++;
            return 1;
        }

        BcRuntime.AwaitIfTask(Body());
        Assert.Equal(2, ran);
    }
}
