using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Pins <see cref="ObservingSubscriberMethodInfo"/>, the decorator
/// <c>EventSubscriberPatches.BuildSubscription</c> hands to BC's
/// <c>NavEventSubscriberMethodInfo</c> ctor so that BC's un-awaited
/// <c>SubscriberMethodInfo.Invoke(...)</c> call site cannot drop an AL error (#2932).
///
/// WHY THIS IS A C# TEST AND NOT AL. Table-event subscribers are dispatched by BC, not by
/// the runner: the runner appends real <c>NavEventSubscription</c> objects to
/// <c>NavEventScope.registeredSubscriptions</c>, and BC's
/// <c>CallEventSubscriberInternalAsync</c> invokes them. It branches on
/// <c>subscriber.MemberId</c>, and the runner has no BC member-id for a scanned
/// <c>[NavEventSubscriber]</c> method, so it passes 0 — the branch that calls
/// <c>SubscriberMethodInfo.Invoke</c> and DISCARDS the result, instead of the awaited
/// <c>InvokeAsync(memberId, …)</c> branch real BC takes.
///
/// The discarded result only matters when the subscriber is an <c>async ValueTask</c> state
/// machine, and the runner's own AL-to-C# emitter emits subscribers as synchronous <c>void</c>
/// methods — measured: in one al-language corpus run, 974 table-event subscriber invocations
/// returned <c>Void</c> and 470 returned <c>ValueTask</c>, and every ValueTask one came from a
/// precompiled Microsoft app. So a first-party AL reproducer cannot reach the broken shape and
/// would pass with and without the fix. The BC-behaviour claim is pinned upstream instead
/// (BusinessCentral.AL.Language.Tests, record/TestTableEventAsyncSubscriberError.al); what is
/// pinned HERE is the runner-side mechanism that makes it hold. Same split, and the same
/// reason, as DispatchObserveAsyncResultTests.cs.
/// </summary>
public class ObservingSubscriberMethodInfoTests
{
    // ---- targets the decorator wraps -------------------------------------------------

    public sealed class Subscriber
    {
        public int SyncCalls;
        public int AsyncCalls;

        public void SyncOk(int x) => SyncCalls++;

        public void SyncThrows() => throw new InvalidOperationException("SYNC-SUBSCRIBER-ERROR");

        [Obsolete("marker attribute target only", false)]
        public async ValueTask AsyncOk()
        {
            AsyncCalls++;
            await Task.Yield();
        }

        public async ValueTask AsyncThrows()
        {
            AsyncCalls++;
            await Task.Yield();
            throw new InvalidOperationException("ASYNC-SUBSCRIBER-ERROR");
        }

        public async Task<int> AsyncGenericThrows()
        {
            await Task.Yield();
            throw new InvalidOperationException("ASYNC-GENERIC-SUBSCRIBER-ERROR");
        }
    }

    private static MethodInfo M(string name) =>
        typeof(Subscriber).GetMethod(name, BindingFlags.Public | BindingFlags.Instance)!;

    private static object? Invoke(MethodInfo mi, object target, params object?[] args)
        => new ObservingSubscriberMethodInfo(mi)
            .Invoke(target, BindingFlags.Default, null, args, null);

    // ---- the defect this type exists for ---------------------------------------------

    [Fact]
    public void AsyncSubscriberThatThrows_SurfacesTheErrorInsteadOfSwallowingIt()
    {
        var target = new Subscriber();

        // The undecorated call is the pre-#2932 behaviour: Invoke RETURNS NORMALLY and the
        // error is reachable only through the returned ValueTask. That value is exactly what
        // BC's memberId==0 branch discarded.
        var raw = (ValueTask)M(nameof(Subscriber.AsyncThrows)).Invoke(target, null)!;
        var rawTask = raw.AsTask();
        var onlyOnTheTask = Assert.Throws<InvalidOperationException>(
            () => rawTask.GetAwaiter().GetResult());
        Assert.Equal("ASYNC-SUBSCRIBER-ERROR", onlyOnTheTask.Message);

        // Decorated, the same call raises — wrapped the way MethodInfo.Invoke wraps a
        // synchronously-thrown exception, so BC's dedicated TargetInvocationException arm
        // (ExceptionDispatchInfo.Capture(inner).Throw()) handles both compile shapes alike.
        var tie = Assert.Throws<TargetInvocationException>(
            () => Invoke(M(nameof(Subscriber.AsyncThrows)), target));
        var inner = Assert.IsType<InvalidOperationException>(tie.InnerException);
        Assert.Equal("ASYNC-SUBSCRIBER-ERROR", inner.Message);
    }

    [Fact]
    public void AsyncSubscriberThatThrows_RunsItsBodyBeforeTheThrow()
    {
        // Not just "an exception came out": the subscriber's observable side effect
        // happened, so the decorator is completing the state machine rather than
        // short-circuiting it.
        var target = new Subscriber();
        Assert.Throws<TargetInvocationException>(
            () => Invoke(M(nameof(Subscriber.AsyncThrows)), target));
        Assert.Equal(1, target.AsyncCalls);
    }

    [Fact]
    public void AsyncGenericSubscriberThatThrows_AlsoSurfacesTheError()
    {
        // Task<T> / ValueTask<T> take a different arm of BcRuntime.ObserveAsyncResult.
        var tie = Assert.Throws<TargetInvocationException>(
            () => Invoke(M(nameof(Subscriber.AsyncGenericThrows)), new Subscriber()));
        Assert.Equal("ASYNC-GENERIC-SUBSCRIBER-ERROR", tie.InnerException!.Message);
    }

    // ---- and the negative half: it must not manufacture failures ---------------------

    [Fact]
    public void AsyncSubscriberThatSucceeds_ReturnsNormallyAndRanItsBody()
    {
        var target = new Subscriber();
        Invoke(M(nameof(Subscriber.AsyncOk)), target);
        Assert.Equal(1, target.AsyncCalls);
    }

    [Fact]
    public void SyncSubscriber_IsUnaffected()
    {
        var target = new Subscriber();
        Invoke(M(nameof(Subscriber.SyncOk)), target, 7);
        Assert.Equal(1, target.SyncCalls);

        // A synchronous throw must still arrive as MethodInfo.Invoke's own
        // TargetInvocationException, NOT double-wrapped by the decorator.
        var tie = Assert.Throws<TargetInvocationException>(
            () => Invoke(M(nameof(Subscriber.SyncThrows)), target));
        var inner = Assert.IsType<InvalidOperationException>(tie.InnerException);
        Assert.Equal("SYNC-SUBSCRIBER-ERROR", inner.Message);
    }

    // ---- and it must stay indistinguishable from the method it wraps ------------------

    [Fact]
    public void DelegatesTheReflectionSurfaceBcReadsOffTheSubscriberMethod()
    {
        // BC reads all of these while building a NavEventSubscription: parameter binding in
        // FindTriggerParameters, DeclaringType checks in NavEventScope's dispatch loop, and
        // the [NavEventSubscriber] attribute in NavEventSubscriberMethodInfo's ctor. If any
        // of them answered for the decorator instead of the inner method, subscriptions
        // would resolve wrongly instead of failing loudly.
        var inner = M(nameof(Subscriber.AsyncOk));
        var wrapped = new ObservingSubscriberMethodInfo(inner);

        Assert.Same(inner, wrapped.Inner);
        Assert.Equal("AsyncOk", wrapped.Name);
        Assert.Equal(typeof(Subscriber), wrapped.DeclaringType);
        Assert.Equal(typeof(ValueTask), wrapped.ReturnType);
        Assert.Equal(inner.MetadataToken, wrapped.MetadataToken);
        Assert.Equal(inner.Module, wrapped.Module);
        Assert.Equal(inner.Attributes, wrapped.Attributes);
        Assert.Equal(
            inner.GetParameters().Select(p => p.ParameterType).ToArray(),
            wrapped.GetParameters().Select(p => p.ParameterType).ToArray());
        Assert.Single(wrapped.GetCustomAttributes(typeof(ObsoleteAttribute), inherit: false));
        Assert.True(wrapped.IsDefined(typeof(ObsoleteAttribute), inherit: false));

        var withParam = new ObservingSubscriberMethodInfo(M(nameof(Subscriber.SyncOk)));
        Assert.Equal(1, withParam.GetParameters().Length);
        Assert.Equal(typeof(int), withParam.GetParameters()[0].ParameterType);
        Assert.Equal(typeof(void), withParam.ReturnType);
    }
}
