// PageTriggerAwaitTests — issue #2359 (a page control's OnValidate error was silently
// discarded because the trigger's ValueTask was thrown away).
//
// This is a RUNNER-MECHANISM test, not a claim about what real BC does. It pins
// RunnerPageInstance.AwaitTriggerResult, the helper every page-trigger dispatch site in
// RunnerPageInstance now routes its MethodInfo.Invoke result through.
//
// The BEHAVIOURAL claim ("a page control's own OnValidate can refuse a value with Error()
// and TestPage SetValue surfaces that error") is proven upstream against a live BC service
// tier — see StefanMaron/BusinessCentral.AL.Language.Tests, codeunit 60820
// "TP Control OnValidate Error".
//
// Why the helper needs pinning at all: BC's AL compiler emits a precompiled page's trigger
// as an async method (measured on Base App page 9170 "Profile Card":
// ProfileIdField_a45_OnValidate returns ValueTask, RoleCenterIdField_a45_OnLookup returns
// ValueTask<bool>). MethodInfo.Invoke hands back the awaitable, so an Error() raised inside
// the trigger is parked on it as a faulted state instead of being thrown at the call site.
// Discarding that value made the runner report success for AL that had plainly failed —
// the silent failure .claude/rules/loud-failures.md forbids.

using System;
using System.Threading.Tasks;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public class PageTriggerAwaitTests
{
    private sealed class TriggerShapeException : Exception
    {
        public TriggerShapeException(string message) : base(message) { }
    }

    // --- the defect, in the exact shape the Base App page produced -------------------

    [Fact]
    public void FaultedValueTask_RethrowsTheOriginalException()
    {
        // `async ValueTask` trigger that raises: the exception lives on the awaitable.
        static async ValueTask Body()
        {
            await Task.Yield();
            throw new TriggerShapeException("the control refuses this value");
        }

        var pending = Body();

        // Not an AggregateException, and not a TargetInvocationException: AL callers —
        // asserterror above all — must observe the error the trigger actually raised.
        var ex = Assert.Throws<TriggerShapeException>(
            () => RunnerPageInstance.AwaitTriggerResult(pending));
        Assert.Equal("the control refuses this value", ex.Message);
    }

    [Fact]
    public void FaultedGenericValueTask_RethrowsTheOriginalException()
    {
        static async ValueTask<bool> Body()
        {
            await Task.Yield();
            throw new TriggerShapeException("the lookup refuses this value");
        }

        var ex = Assert.Throws<TriggerShapeException>(
            () => RunnerPageInstance.AwaitTriggerResult(Body()));
        Assert.Equal("the lookup refuses this value", ex.Message);
    }

    [Fact]
    public void FaultedTask_RethrowsTheOriginalException()
    {
        static async Task Body()
        {
            await Task.Yield();
            throw new TriggerShapeException("task-shaped trigger refused");
        }

        var ex = Assert.Throws<TriggerShapeException>(
            () => RunnerPageInstance.AwaitTriggerResult(Body()));
        Assert.Equal("task-shaped trigger refused", ex.Message);
    }

    // --- the OnLookup half: the awaitable's VALUE has to come back -------------------

    [Fact]
    public void GenericValueTask_YieldsTheValueTheTriggerReturned()
    {
        static async ValueTask<bool> Accepted()
        {
            await Task.Yield();
            return true;
        }

        static async ValueTask<bool> Cancelled()
        {
            await Task.Yield();
            return false;
        }

        // RaiseOnLookup tests `result is true`. Before this helper existed it tested that
        // against the ValueTask<bool> itself, which is never true, so every lookup on a
        // precompiled page read as "the user cancelled" no matter what the trigger returned.
        Assert.Equal(true, RunnerPageInstance.AwaitTriggerResult(Accepted()));
        Assert.Equal(false, RunnerPageInstance.AwaitTriggerResult(Cancelled()));
    }

    [Fact]
    public void GenericTask_YieldsTheValueTheTriggerReturned()
    {
        static async Task<bool> Accepted()
        {
            await Task.Yield();
            return true;
        }

        Assert.Equal(true, RunnerPageInstance.AwaitTriggerResult(Accepted()));
    }

    // --- completing normally, and the non-async emit shape ---------------------------

    [Fact]
    public void CompletedValueTask_YieldsNullAndDoesNotThrow()
    {
        Assert.Null(RunnerPageInstance.AwaitTriggerResult(default(ValueTask)));
        Assert.Null(RunnerPageInstance.AwaitTriggerResult(Task.CompletedTask));
        Assert.Null(RunnerPageInstance.AwaitTriggerResult(null));
    }

    [Fact]
    public void NonAwaitableResult_IsPassedThroughUnchanged()
    {
        // The runner's OWN AL emit produces `void` page triggers (measured: source-compiled
        // page 64901's ControlGuarded_a45_OnValidate returns Void), so a trigger that is not
        // async at all must keep working, and a hypothetical non-async Boolean OnLookup must
        // still answer with its bool rather than being swallowed.
        Assert.Equal(true, RunnerPageInstance.AwaitTriggerResult(true));
        Assert.Equal("plain", RunnerPageInstance.AwaitTriggerResult("plain"));
        Assert.Equal(7, RunnerPageInstance.AwaitTriggerResult(7));
    }

    [Fact]
    public void ADeferredTriggerIsWaitedFor_NotJustStarted()
    {
        // The other half of the same defect: a discarded awaitable is also an UNFINISHED one.
        // A trigger that suspends must have completed its side effects before the helper
        // returns, or the AL that follows SetValue observes a half-run trigger.
        var landed = false;

        async ValueTask Body()
        {
            await Task.Delay(30);
            landed = true;
        }

        RunnerPageInstance.AwaitTriggerResult(Body());
        Assert.True(landed, "AwaitTriggerResult must block until the trigger has finished");
    }
}
