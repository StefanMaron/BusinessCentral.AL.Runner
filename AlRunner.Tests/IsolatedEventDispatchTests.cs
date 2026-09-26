using System;
using System.IO;
using System.Reflection;
using AlRunner;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Types.Exceptions;
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

    // IsTrappedByIsolatedEvent: which errors an isolated subscriber swallows. BC traps every
    // NavBaseException unless the session is cancelled; the runner's own refusals must stay loud
    // even when BC has rewrapped them as a NavBaseException (loud-failures.md).

    private static Exception Wrapped(Exception inner) => new NavALException("wrapped", inner);

    private static BcShapeGapException ShapeGap() => new("surface", "Type.member", "detail");

    private static BcAppSymbolReadException SymbolRead()
        => new("/x/dep.app", "table symbols", new InvalidDataException("bad json"));

    [Fact]
    public void PlainNavALException_IsTrapped()
        => Assert.True(BcRuntime.IsTrappedByIsolatedEvent(new NavALException("subscriber failed"), sessionCancelled: false));

    [Fact]
    public void PlainNavALException_BehindTargetInvocation_IsTrapped()
        => Assert.True(BcRuntime.IsTrappedByIsolatedEvent(
            new TargetInvocationException(new NavALException("subscriber failed")), sessionCancelled: false));

    [Fact]
    public void ShapeGapWrappedInNavALException_IsNotTrapped()
        => Assert.False(BcRuntime.IsTrappedByIsolatedEvent(Wrapped(ShapeGap()), sessionCancelled: false));

    [Fact]
    public void ShapeGapWrappedInNavALException_BehindTargetInvocation_IsNotTrapped()
        => Assert.False(BcRuntime.IsTrappedByIsolatedEvent(
            new TargetInvocationException(Wrapped(ShapeGap())), sessionCancelled: false));

    [Fact]
    public void SymbolReadWrappedInNavALException_IsNotTrapped()
        => Assert.False(BcRuntime.IsTrappedByIsolatedEvent(Wrapped(SymbolRead()), sessionCancelled: false));

    [Fact]
    public void SymbolReadWrappedInNavALException_BehindTargetInvocation_IsNotTrapped()
        => Assert.False(BcRuntime.IsTrappedByIsolatedEvent(
            new TargetInvocationException(Wrapped(SymbolRead())), sessionCancelled: false));

    [Fact]
    public void RunnerOutOfScope_IsNotTrapped()
        => Assert.False(BcRuntime.IsTrappedByIsolatedEvent(
            new TargetInvocationException(new RunnerOutOfScopeException("NavEmail.Send", "email-smtp")),
            sessionCancelled: false));

    [Fact]
    public void NavALException_OnCancelledSession_IsNotTrapped()
        => Assert.False(BcRuntime.IsTrappedByIsolatedEvent(
            new NavALException("subscriber failed"), sessionCancelled: true));
}
