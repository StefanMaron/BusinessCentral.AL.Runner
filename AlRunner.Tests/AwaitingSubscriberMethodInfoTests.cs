using System;
using System.Reflection;
using System.Threading.Tasks;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #2932 — the runner builds every injected table-event NavEventSubscription with
/// memberId 0, which sends BC's NavEventScope.CallEventSubscriberInternalAsync down
///
///     subscriber.SubscriberMethodInfo.Invoke(subscriberInstance, parameters);
///
/// and that branch discards the return value. Every Base Application / System Application
/// subscriber on table 2000000120 measured on BC 28.1 is emitted as an async ValueTask method
/// (Codeunit9002.CheckCurrentUserCanModifyUser, Codeunit418.ValidateLicenseTypeOnAfterInsertUser,
/// Codeunit153.CheckSuperPermissionsBeforeModifyUser), so dropping that ValueTask abandoned the
/// body at its first suspension and swallowed the Error() it raised.
///
/// The end-to-end claim is pinned in AL by tests/runner-extras/precompiled-async-subscriber,
/// which drives Base Application codeunit 9002 through a real Modify. This class pins the seam
/// itself: the forwarding contract BC's dispatch depends on, and the exception shape, neither of
/// which an AL test can observe directly.
/// </summary>
public class AwaitingSubscriberMethodInfoTests
{
    private static MethodInfo M(string name) =>
        typeof(AwaitingSubscriberMethodInfoTests).GetMethod(name,
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    private int _reachedAfterAwait;

    [Obsolete("marker attribute for the forwarding test — never called as obsolete")]
    private void SyncSubscriber(int a, string b) { _reachedAfterAwait = a + b.Length; }

    private async ValueTask AsyncSubscriberThatCompletes()
    {
        await Task.Yield();
        _reachedAfterAwait = 42;
    }

    private async ValueTask AsyncSubscriberThatThrowsAfterAwait()
    {
        await Task.Yield();
        throw new InvalidOperationException("User Name must have a value");
    }

    private async Task<int> AsyncSubscriberReturningValue()
    {
        await Task.Yield();
        return 7;
    }

    // ── which methods get wrapped ────────────────────────────────────────────────────────

    [Fact]
    public void NeedsAwaiting_IsTrueOnlyForAwaitableReturnTypes()
    {
        Assert.False(AwaitingSubscriberMethodInfo.NeedsAwaiting(M(nameof(SyncSubscriber))));
        Assert.True(AwaitingSubscriberMethodInfo.NeedsAwaiting(M(nameof(AsyncSubscriberThatCompletes))));
        Assert.True(AwaitingSubscriberMethodInfo.NeedsAwaiting(M(nameof(AsyncSubscriberReturningValue))));
    }

    [Fact]
    public void WrapIfAwaitable_LeavesAVoidMethodExactlyAsItWas()
    {
        var raw = M(nameof(SyncSubscriber));

        // Reference equality, not "some MethodInfo": the common source-compiled `void`
        // subscriber must reach BC on the identical object it reached before this change.
        Assert.Same(raw, AwaitingSubscriberMethodInfo.WrapIfAwaitable(raw));
    }

    [Fact]
    public void WrapIfAwaitable_WrapsAnAsyncMethod()
    {
        var raw = M(nameof(AsyncSubscriberThatCompletes));
        var wrapped = AwaitingSubscriberMethodInfo.WrapIfAwaitable(raw);

        Assert.NotSame(raw, wrapped);
        Assert.Same(raw, Assert.IsType<AwaitingSubscriberMethodInfo>(wrapped).Inner);
    }

    // ── the behaviour the fix exists for ─────────────────────────────────────────────────

    [Fact]
    public void Invoke_RunsAnAsyncBodyPastItsFirstAwait()
    {
        _reachedAfterAwait = 0;
        var wrapped = AwaitingSubscriberMethodInfo.WrapIfAwaitable(M(nameof(AsyncSubscriberThatCompletes)));

        wrapped.Invoke(this, null);

        // 42 is assigned only after `await Task.Yield()`. The raw MethodInfo returns as soon as
        // the state machine suspends, so without the wrapper this reads 0 — which is exactly how
        // a Base App subscriber's validation silently did not happen.
        Assert.Equal(42, _reachedAfterAwait);
    }

    [Fact]
    public void Invoke_SurfacesAnExceptionRaisedAfterTheFirstAwait()
    {
        var wrapped = AwaitingSubscriberMethodInfo.WrapIfAwaitable(M(nameof(AsyncSubscriberThatThrowsAfterAwait)));

        var tie = Assert.Throws<TargetInvocationException>(() => wrapped.Invoke(this, null));

        // TargetInvocationException wrapping the ORIGINAL exception is what BC's
        // CallEventSubscriberInternalAsync unwraps with ExceptionDispatchInfo, so a
        // NavBaseException raised by AL is rethrown verbatim to the AL caller. An
        // AggregateException, or the bare exception, would take a different arm there.
        var inner = Assert.IsType<InvalidOperationException>(tie.InnerException);
        Assert.Equal("User Name must have a value", inner.Message);
    }

    [Fact]
    public void Invoke_StillReturnsTheTaskObjectToTheCaller()
    {
        var wrapped = AwaitingSubscriberMethodInfo.WrapIfAwaitable(M(nameof(AsyncSubscriberReturningValue)));

        var result = wrapped.Invoke(this, null);

        // BC discards this, but the decorator must not change the reflection contract.
        var task = Assert.IsAssignableFrom<Task<int>>(result);
        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(7, task.Result);
    }

    [Fact]
    public void Invoke_PassesArgumentsThrough()
    {
        // A sync method is not wrapped, so exercise the decorator's Invoke directly by
        // wrapping an awaitable one and checking the argument-bearing path on the async body.
        _reachedAfterAwait = 0;
        var wrapped = AwaitingSubscriberMethodInfo.WrapIfAwaitable(M(nameof(AsyncSubscriberWithArgs)));

        wrapped.Invoke(this, new object?[] { 5, "abc" });

        Assert.Equal(8, _reachedAfterAwait);
    }

    private async ValueTask AsyncSubscriberWithArgs(int a, string b)
    {
        await Task.Yield();
        _reachedAfterAwait = a + b.Length;
    }

    // ── the forwarding contract BC's dispatch reads ──────────────────────────────────────

    [Fact]
    public void ForwardsTheMetadataBcReadsDuringDispatch()
    {
        var raw = M(nameof(AsyncSubscriberWithArgs));
        var wrapped = AwaitingSubscriberMethodInfo.WrapIfAwaitable(raw);

        // NavEventSubscription compares SubscriberMethodInfo.DeclaringType by REFERENCE against
        // the subscriber instance's type, and NavEventSubscriberMethodInfo's ctor reads the
        // custom attributes and the AL name off this object. Names/parameters feed
        // FindTriggerParameters, which decides whether the subscription resolves at all.
        Assert.Same(raw.DeclaringType, wrapped.DeclaringType);
        Assert.Equal(raw.Name, wrapped.Name);
        Assert.Same(raw.ReturnType, wrapped.ReturnType);
        Assert.Equal(raw.Attributes, wrapped.Attributes);
        Assert.Equal(raw.MetadataToken, wrapped.MetadataToken);
        Assert.Same(raw.Module, wrapped.Module);

        var rawPs = raw.GetParameters();
        var wrappedPs = wrapped.GetParameters();
        Assert.Equal(rawPs.Length, wrappedPs.Length);
        for (var i = 0; i < rawPs.Length; i++)
        {
            Assert.Equal(rawPs[i].Name, wrappedPs[i].Name);
            Assert.Same(rawPs[i].ParameterType, wrappedPs[i].ParameterType);
        }
    }

    [Fact]
    public void ForwardsCustomAttributes()
    {
        // The whole dispatch hangs off NavEventSubscriberAttribute being readable from the
        // wrapper: NavEventSubscriberMethodInfo's ctor does GetCustomAttribute<T>() on it, and a
        // null there leaves NavEventSubscription with a null SubscriberMethodAttribute.
        var raw = M(nameof(SyncSubscriber));
        var wrapped = new WrapperUnderTest(raw);

        Assert.NotNull(wrapped.GetCustomAttribute<ObsoleteAttribute>());
        Assert.True(wrapped.IsDefined(typeof(ObsoleteAttribute), inherit: false));
        Assert.Equal(raw.GetCustomAttributes(inherit: false).Length,
            wrapped.GetCustomAttributes(inherit: false).Length);
    }

    /// <summary>
    /// <see cref="AwaitingSubscriberMethodInfo.WrapIfAwaitable"/> deliberately refuses to wrap a
    /// non-awaitable method, so the attribute-forwarding claim above is exercised through the
    /// same ctor via this shim rather than by relaxing the production guard.
    /// </summary>
    private sealed class WrapperUnderTest
    {
        private readonly MethodInfo _wrapped;
        public WrapperUnderTest(MethodInfo raw)
        {
            var ctor = typeof(AwaitingSubscriberMethodInfo).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(MethodInfo) }, null)
                ?? throw new InvalidOperationException("AwaitingSubscriberMethodInfo(MethodInfo) not found");
            _wrapped = (MethodInfo)ctor.Invoke(new object[] { raw });
        }
        public T? GetCustomAttribute<T>() where T : Attribute => _wrapped.GetCustomAttribute<T>();
        public bool IsDefined(Type t, bool inherit) => _wrapped.IsDefined(t, inherit);
        public object[] GetCustomAttributes(bool inherit) => _wrapped.GetCustomAttributes(inherit);
    }
}
