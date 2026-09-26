using System;
using AlRunner;
using Microsoft.Dynamics.Nav.Types;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// The runner-side half of #4721: which events the dispatcher treats as isolated, read from the
/// same <c>NavEventAttribute.Isolated</c> BC reads. The BC behaviour itself (error trapped, writes rolled back, caller
/// with a pending write not isolated) is pinned upstream by corpus 67103.
/// </summary>
public class IsolatedEventDispatchTests
{
    // Shaped like AL's emitted output: the publisher method carries NavEventAttribute and the
    // scope class the dispatcher receives is nested in the publisher type.
    private sealed class Publisher
    {
        [NavEvent(NavEventType.Integration, false, false, true)]
        public void OnIsolatedWork() { }

        [NavEvent(NavEventType.Integration, false, false, false)]
        public void OnSharedWork() { }

        public void OnUnattributed() { }

        public sealed class OnIsolatedWork_Scope { }
        public sealed class OnSharedWork_Scope { }
        public sealed class OnUnattributed_Scope { }
    }

    [Fact]
    public void EventDeclaredIsolated_IsReadAsIsolated()
        => Assert.True(BcRuntime.IsIsolatedEventScope(typeof(Publisher.OnIsolatedWork_Scope), "OnIsolatedWork"));

    [Fact]
    public void EventDeclaredNotIsolated_IsReadAsNotIsolated()
        => Assert.False(BcRuntime.IsIsolatedEventScope(typeof(Publisher.OnSharedWork_Scope), "OnSharedWork"));

    [Fact]
    public void PublisherMethodWithoutNavEventAttribute_IsReadAsNotIsolated()
        => Assert.False(BcRuntime.IsIsolatedEventScope(typeof(Publisher.OnUnattributed_Scope), "OnUnattributed"));
}
