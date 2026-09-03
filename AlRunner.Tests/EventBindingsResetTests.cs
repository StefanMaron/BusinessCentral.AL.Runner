// EventBindingsResetTests — pins BcRuntime.ResetEventBindingsForTestBoundary(), the
// mechanism half of AL Runner issue #2466.
//
// The bug it fixes
// -----------------
// A manual event subscription (EventSubscriberInstance = Manual) bound via
// BindSubscription and never explicitly unbound stays in real BC's own
// Session.EventBindings list for as long as the codeunit RUN that made the binding is
// alive. RecordPatches.ResetPerTestState() already tears down everything else that must
// not leak across a codeunit/Test-isolation boundary (SingleInstance codeunit instances,
// the DataAccess store, IsolatedStorage, …) but never touched EventBindings, so a leaked
// manual subscription from one test CODEUNIT kept firing in every codeunit that ran
// after it for the rest of the process. Base App codeunit 9178 "Application Area Mgmt."
// hit this concretely: a manual subscriber left bound by an earlier test codeunit cleared
// "Application Area Setup".Basic underneath SetExperienceTier, failing its TESTFIELD in
// 18 Tests-SINGLESERVER tests it had nothing to do with.
//
// Why this is a runner-side test and not (only) an AL corpus test
// -----------------------------------------------------------------
// The AL-level claim — "a binding left open by one test codeunit does not survive into
// the next test codeunit's run, but a binding left open within one codeunit DOES survive
// across its own [Test] procedures" — is plain BC behaviour and belongs upstream: see
// TestEventManualBindingCrossCodeunit (60244/60245) and TestEventManualBinding's new
// Contract 10, submitted against StefanMaron/BusinessCentral.AL.Language.Tests. This file
// pins the RUNNER'S OWN bookkeeping instead — that ResetEventBindingsForTestBoundary
// actually reaches and clears Session.EventBindings when RecordPatches.ResetPerTestState()
// runs, and does nothing (rather than throw) when the skeleton session or its EventBindings
// property is unavailable — which no AL test can address directly, since AL has no way to
// invoke ResetPerTestState() out of turn.
using System.Reflection;
using AlRunner;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Minimal stand-in bound entry for the tests below. Session.EventBindings is a real
/// <c>List&lt;NavCodeunit&gt;</c> (see BcRuntime.cs's skeleton-session init comment), so a
/// plain <c>object()</c> throws <c>ArgumentException</c> the moment IList.Add tries to
/// store it — measured for real in CI (#2472). Named <c>Codeunit69002</c> for the same
/// reason SingleInstanceResetHandleInvalidationTests' Codeunit69001 is: BcRuntime.
/// FindCodeunitType resolves an id to the type literally named <c>Codeunit{id}</c>, and
/// 69002 sits outside every AL idRange this repo declares, so it can never collide with a
/// compiled AL object. Not marked IsEventManualBinding/IsSubscriptionBound — the reset
/// under test clears the list unconditionally, so it doesn't need a faithful bind/unbind
/// dance, only a type the list will actually accept.
/// </summary>
internal sealed class Codeunit69002 : NavCodeunit
{
    public Codeunit69002(ITreeObject parent) : base(parent, 69002) { }
}

// Loads Ncl types in-process (BcRuntime.SkeletonSession, EventBindings' declaring type),
// so it shares the serial bc-engine collection with everything else that Cecil-rewrites/
// loads Ncl.dll. See BcEngineCollection.cs.
[Collection(BcEngineCollection.Name)]
public class EventBindingsResetTests
{
    private readonly BcEngineFixture _engine;

    public EventBindingsResetTests(BcEngineFixture engine) => _engine = engine;

    private static ITreeObject Root()
    {
        var root = BcRuntime.RootTreeStub;
        Assert.True(root != null,
            "BcRuntime.RootTreeStub is null after the engine bootstrap — the skeleton tree " +
            "these stand-in codeunits must be parented on does not exist, so this test cannot " +
            "run at all.");
        return root!;
    }

    private static PropertyInfo EventBindingsProperty()
    {
        var session = BcRuntime.SkeletonSession;
        Assert.True(session != null,
            "BcRuntime.SkeletonSession is null after the engine bootstrap — the skeleton " +
            "session EventBindings lives on does not exist, so this test cannot run at all.");
        var prop = session!.GetType().GetProperty("EventBindings",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(prop != null,
            "Microsoft.Dynamics.Nav.Runtime.NavSession.EventBindings was not found by " +
            "reflection — the Ncl shape BcRuntime.ResetEventBindingsForTestBoundary and " +
            "CodeunitEventDispatcher.BoundInstancesOf both depend on has changed.");
        return prop!;
    }

    private static System.Collections.IList EventBindings()
        => (System.Collections.IList)EventBindingsProperty().GetValue(BcRuntime.SkeletonSession)!;

    /// <summary>
    /// Positive: a non-empty EventBindings list is emptied by the reset. The stand-in
    /// entries are real NavCodeunit instances (not a plain object() — Session.EventBindings
    /// is a genuine List&lt;NavCodeunit&gt; and rejects anything else), but not bound via a
    /// faithful BindSubscription dance — the reset only cares that the list is cleared, the
    /// same as BC's own IList.Clear() would do to any entries a real BindSubscription had
    /// added.
    /// </summary>
    [SkippableFact]
    public void NonEmptyEventBindings_AreClearedAtTheTestBoundary()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var bindings = EventBindings();
        var root = Root();
        bindings.Add(new Codeunit69002(root));
        bindings.Add(new Codeunit69002(root));
        Assert.Equal(2, bindings.Count);

        BcRuntime.ResetEventBindingsForTestBoundary();

        Assert.Equal(0, bindings.Count);
    }

    /// <summary>
    /// Negative-shaped: calling the reset on an ALREADY-empty list must not throw and must
    /// leave it empty — the common case (most codeunit boundaries have nothing leaked to
    /// clear) must be a cheap no-op, not an error.
    /// </summary>
    [SkippableFact]
    public void AlreadyEmptyEventBindings_ResetIsANoOp()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var bindings = EventBindings();
        bindings.Clear();
        Assert.Equal(0, bindings.Count);

        var ex = Record.Exception(BcRuntime.ResetEventBindingsForTestBoundary);

        Assert.Null(ex);
        Assert.Equal(0, bindings.Count);
    }

    /// <summary>
    /// The reset survives being called before the skeleton session exists at all (e.g. a
    /// unit test host that never bootstrapped the engine) — SkeletonSession is null in
    /// that case, and ResetEventBindingsForTestBoundary must return quietly rather than
    /// NRE. Runs unconditionally: it is exactly the no-engine case, so it must NOT be
    /// gated behind _engine.Ready.
    /// </summary>
    [Fact]
    public void NoSkeletonSession_ResetDoesNotThrow()
    {
        if (BcRuntime.SkeletonSession != null)
            return; // engine bootstrap already ran in this process; covered by the tests above instead.

        var ex = Record.Exception(BcRuntime.ResetEventBindingsForTestBoundary);

        Assert.Null(ex);
    }
}
