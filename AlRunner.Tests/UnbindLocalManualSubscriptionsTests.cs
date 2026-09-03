// UnbindLocalManualSubscriptionsTests — pins BcRuntime.UnbindLocalManualSubscriptions()
// (MethodScopePatches.cs), the mechanism half of AL Runner issue #2476.
//
// The bug it fixes
// -----------------
// A manual event subscription (EventSubscriberInstance = Manual) bound via
// BindSubscription through a LOCAL "var X: Codeunit ..." variable never released when the
// declaring [Test] procedure returned. Real BC's own NavCodeunit.Dispose(bool) does exactly
// that — clears IsSubscriptionBound and removes the instance from Session.EventBindings —
// when the codeunit instance itself is disposed, and a local variable's underlying instance
// IS disposed at that point. The runner's MEMORY LEAK FIX in NavMethodScope_Dispose
// deliberately does NOT cascade a full Dispose() into a disposing scope's children (a full
// cascade corrupted BLOB/stream data legitimately escaping the scope — see that method's own
// doc comment), so the disposal that would have released the binding never fired.
//
// Why this is a runner-side test and not (only) an AL corpus test
// -----------------------------------------------------------------
// The AL-level claim — "a binding made through a LOCAL codeunit variable does not survive
// into the next [Test] on the same codeunit, unlike a GLOBAL variable's binding, which does"
// — is plain BC behaviour and belongs upstream: see TestEventManualBinding's Contract 9/10,
// submitted against StefanMaron/BusinessCentral.AL.Language.Tests#110. This file pins the
// RUNNER'S OWN bookkeeping instead: that UnbindLocalManualSubscriptions actually walks a
// disposing scope's own direct NavCodeunitHandle children, clears IsSubscriptionBound and
// removes the bound target from Session.EventBindings for one that is still bound, leaves an
// UNBOUND child's target untouched, and never touches a non-NavCodeunitHandle child at all —
// none of which any AL test can address directly, since AL has no way to invoke this sweep
// out of turn or to inspect Session.EventBindings itself.
using System.Linq;
using System.Reflection;
using AlRunner;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Minimal stand-in NavCodeunit, constructed via the "already resolved" NavCodeunitHandle
/// ctor (parent, initialTarget) so the test never touches real NCLMetadata resolution —
/// mirroring EventBindingsResetTests' own Codeunit69002. Named Codeunit69003 for the same
/// reason: BcRuntime.FindCodeunitType resolves an id to the type literally named
/// Codeunit{id}, and 69003 sits outside every AL idRange this repo declares, so it can never
/// collide with a compiled AL object.
/// </summary>
internal sealed class Codeunit69003 : NavCodeunit
{
    public Codeunit69003(ITreeObject parent) : base(parent, 69003) { }
}

// Loads Ncl types in-process (BcRuntime.SkeletonSession, RootTreeStub, NavCodeunit's own
// IsSubscriptionBound), so it shares the serial bc-engine collection with everything else
// that Cecil-rewrites/loads Ncl.dll. See BcEngineCollection.cs.
[Collection(BcEngineCollection.Name)]
public class UnbindLocalManualSubscriptionsTests
{
    private readonly BcEngineFixture _engine;

    public UnbindLocalManualSubscriptionsTests(BcEngineFixture engine) => _engine = engine;

    private static ITreeObject Root()
    {
        var root = BcRuntime.RootTreeStub;
        Assert.True(root != null,
            "BcRuntime.RootTreeStub is null after the engine bootstrap — the skeleton tree " +
            "these stand-in codeunits must be parented on does not exist, so this test cannot " +
            "run at all.");
        return root!;
    }

    private static System.Collections.IList EventBindings()
    {
        var bindings = BcRuntime.SessionEventBindings();
        Assert.True(bindings != null,
            "BcRuntime.SessionEventBindings() returned null — either BcRuntime.SkeletonSession " +
            "is null after the engine bootstrap, or Microsoft.Dynamics.Nav.Runtime.NavSession." +
            "EventBindings was not found by reflection.");
        return bindings!;
    }

    private static PropertyInfo IsSubscriptionBoundProperty()
    {
        var prop = typeof(NavCodeunit).GetProperty("IsSubscriptionBound",
            BindingFlags.Public | BindingFlags.Instance);
        Assert.True(prop != null,
            "Microsoft.Dynamics.Nav.Runtime.NavCodeunit.IsSubscriptionBound was not found by " +
            "reflection — the Ncl shape UnbindLocalManualSubscriptions depends on has changed.");
        return prop!;
    }

    private static void SetSubscriptionBound(NavCodeunit target, bool value)
        => IsSubscriptionBoundProperty().GetSetMethod(nonPublic: true)!.Invoke(target, new object[] { value });

    /// <summary>
    /// Positive: a scope whose direct child is a NavCodeunitHandle resolving to a
    /// STILL-BOUND target has that target's binding released — IsSubscriptionBound flips to
    /// false and it is removed from Session.EventBindings — exactly what real BC's own
    /// Dispose(bool) does for a disposed, still-bound codeunit instance.
    /// </summary>
    [SkippableFact]
    public void BoundLocalVariableHandle_IsUnbound()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var root = Root();
        var scope = new NavScope(root);
        var target = new Codeunit69003(root);
        _ = new NavCodeunitHandle(scope, target); // parents the handle on `scope`, Target = target

        SetSubscriptionBound(target, true);
        var bindings = EventBindings();
        bindings.Add(target);
        Assert.Contains(target, bindings.Cast<object>());
        Assert.True(target.IsSubscriptionBound);

        BcRuntime.UnbindLocalManualSubscriptions(scope);

        Assert.False(target.IsSubscriptionBound,
            "UnbindLocalManualSubscriptions must clear IsSubscriptionBound on a still-bound " +
            "local-variable target, mirroring NavCodeunit.Dispose(bool).");
        Assert.DoesNotContain(target, bindings.Cast<object>());
    }

    /// <summary>
    /// Negative: a scope whose direct child is a NavCodeunitHandle resolving to a target
    /// that was NEVER bound is left completely alone — the sweep must not touch
    /// IsSubscriptionBound or Session.EventBindings for a handle with nothing to release.
    /// This would catch an implementation that unconditionally clears every NavCodeunitHandle
    /// child it finds, rather than checking IsSubscriptionBound first.
    /// </summary>
    [SkippableFact]
    public void UnboundLocalVariableHandle_IsLeftAlone()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var root = Root();
        var scope = new NavScope(root);
        var target = new Codeunit69003(root);
        _ = new NavCodeunitHandle(scope, target);

        Assert.False(target.IsSubscriptionBound);
        var bindings = EventBindings();
        var countBefore = bindings.Count;

        var ex = Record.Exception(() => BcRuntime.UnbindLocalManualSubscriptions(scope));

        Assert.Null(ex);
        Assert.False(target.IsSubscriptionBound);
        Assert.Equal(countBefore, bindings.Count);
    }

    /// <summary>
    /// A GLOBAL codeunit-typed variable is a field on the containing test codeunit
    /// INSTANCE, never a child of a per-call method scope, so it must never be reachable
    /// through this sweep at all — a target bound via a handle that is NOT a child of the
    /// disposing scope (parented on `root` directly here, standing in for "some other tree
    /// entirely") survives untouched. This is what keeps TestEventManualBinding Contract 9
    /// (a GLOBAL variable's binding survives across [Test]s in the same codeunit) intact.
    /// </summary>
    [SkippableFact]
    public void HandleNotParentedOnTheDisposingScope_TargetSurvives()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var root = Root();
        var scope = new NavScope(root); // the "disposing" scope — has no children of its own
        var target = new Codeunit69003(root);
        _ = new NavCodeunitHandle(root, target); // parented on ROOT, not on `scope`

        SetSubscriptionBound(target, true);
        var bindings = EventBindings();
        bindings.Add(target);

        BcRuntime.UnbindLocalManualSubscriptions(scope);

        Assert.True(target.IsSubscriptionBound,
            "A handle that is not a child of the disposing scope must survive untouched — " +
            "this is what keeps a GLOBAL variable's binding (Contract 9) alive across [Test]s.");
        Assert.Contains(target, bindings.Cast<object>());

        // Clean up so this binding does not leak into a later test in the same process.
        SetSubscriptionBound(target, false);
        bindings.Remove(target);
    }

    /// <summary>
    /// The sweep survives being called with a scope that has no reflected tree fields
    /// resolved at all (e.g. a unit test host that never bootstrapped the engine) — must
    /// return quietly rather than NRE. Runs unconditionally, mirroring
    /// EventBindingsResetTests.NoSkeletonSession_ResetDoesNotThrow.
    /// </summary>
    [Fact]
    public void NullScope_DoesNotThrow()
    {
        var ex = Record.Exception(() => BcRuntime.UnbindLocalManualSubscriptions(null));

        Assert.Null(ex);
    }
}
