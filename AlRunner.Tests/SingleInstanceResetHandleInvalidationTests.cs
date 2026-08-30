// SingleInstanceResetHandleInvalidationTests — pins the invariant that makes a
// SingleInstance codeunit have exactly ONE live instance at any moment, across BOTH of
// the places the runner records which instance that is.
//
// The two places (this is the bug)
// --------------------------------
// 1. BcRuntime._singleInstanceCache — codeunit id -> instance, consulted by
//    BcRuntime.NavCodeunitHandle_CreateTarget (our replacement for
//    NavCodeunitHandle.CreateTarget).
// 2. Every AL variable's own handle. NavApplicationObjectBaseHandle<T>.get_Target is
//    unmodified BC code and reads:
//
//        target = Tree.GetReferenceTarget();
//        if (target == null && !Tree.IsDisposed) {
//            target = CreateTarget();              // <- our patch
//            Tree.SetReferenceTarget(target);      // <- second copy of the answer
//        }
//        return target;
//
//    So a handle calls CreateTarget exactly ONCE and remembers what it got, for as long
//    as the AL variable holding it lives.
//
// ResetSingleInstanceCache() used to maintain only (1). Any handle that had already
// resolved kept answering with the dropped instance, so AL writing through a handle bound
// before the reset and AL reading through a handle bound after it silently addressed two
// different objects — the writer's state was invisible to the reader, and neither side
// had any way to notice.
//
// Why this is a runner-side test and not an AL corpus test
// -------------------------------------------------------
// The AL-level claim ("a SingleInstance codeunit keeps its state across a callback in the
// same test") is plain BC behaviour and is already covered upstream — corpus codeunits
// 60237 "Test Event ByRef Mut" and 60922 "ASK Tests" both write a SingleInstance codeunit
// from a test method and read it back from a callback. What THIS file pins is the runner's
// own cache/handle bookkeeping, which no AL test can address directly: the divergence is
// only reachable when a reset fires while an AL object holding a bound handle is still
// alive, and which moments those are is a property of TestExecutor's isolation wiring, not
// of AL. It was reachable in #2144 (a per-test reset under Codeunit isolation, where the
// test codeunit instance is shared by every test in the codeunit) and cost 15 corpus
// tests; #2161 changed the reset placement, which hid it again without fixing it. This
// test asserts the invariant directly so the next isolation change cannot re-expose it.

using AlRunner;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// A minimal SingleInstance codeunit for the tests below. Named <c>Codeunit69001</c>
/// because BcRuntime.FindCodeunitType resolves a codeunit id to the type literally named
/// <c>Codeunit{id}</c>; 69001 is outside every AL idRange this repo declares, so it can
/// never collide with a compiled AL object. IsSingleInstance is BC's own public virtual
/// property — overriding it here is exactly what BC's AL emitter does for a codeunit
/// declaring <c>SingleInstance = true</c>, so CreateTarget's real, unmodified
/// <c>instance.IsSingleInstance</c> read sees faithful metadata.
/// </summary>
internal sealed class Codeunit69001 : NavCodeunit
{
    public Codeunit69001(ITreeObject parent) : base(parent, 69001) { }

    public override bool IsSingleInstance => true;

    /// <summary>Stands in for an AL instance variable — the state whose visibility is
    /// the whole point of SingleInstance.</summary>
    public string Token { get; set; } = string.Empty;
}

// Loads Ncl types in-process, so it shares the serial bc-engine collection with the class
// that Cecil-rewrites Ncl.dll on disk. See BcEngineCollection.cs.
[Collection(BcEngineCollection.Name)]
public class SingleInstanceResetHandleInvalidationTests
{
    private const int ProbeId = 69001;

    private readonly BcEngineFixture _engine;

    public SingleInstanceResetHandleInvalidationTests(BcEngineFixture engine) => _engine = engine;

    private ITreeObject Root()
    {
        var root = BcRuntime.RootTreeStub;
        Assert.True(root != null,
            "BcRuntime.RootTreeStub is null after the engine bootstrap — the skeleton tree these " +
            "handles must be parented on does not exist, so this test cannot run at all.");
        return root!;
    }

    /// <summary>
    /// Positive: within one reset window every handle — however many AL variables name the
    /// codeunit — resolves to the SAME instance, so state written through one is visible
    /// through the other. This is the property SingleInstance exists to provide, and it is
    /// also the guard against "fix" the invalidation by simply never caching.
    /// </summary>
    [SkippableFact]
    public void WithinOneResetWindow_EveryHandleResolvesTheSameInstance()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        BcRuntime.ResetSingleInstanceCache();
        var root = Root();

        var writer = new NavCodeunitHandle(root, ProbeId);
        var written = Assert.IsType<Codeunit69001>(writer.Target);
        written.Token = "WINDOW-1";

        var reader = new NavCodeunitHandle(root, ProbeId);
        var read = Assert.IsType<Codeunit69001>(reader.Target);

        Assert.Same(written, read);
        Assert.Equal("WINDOW-1", read.Token);
    }

    /// <summary>
    /// The bug. A handle that resolved BEFORE ResetSingleInstanceCache() must not keep
    /// handing back the instance the reset dropped — it must re-resolve and agree with a
    /// handle bound after the reset.
    ///
    /// Asserts both directions of the same identity, because either alone is satisfiable
    /// by a broken implementation: NotSame(before, after) alone passes today (the reset
    /// does drop the cache entry), and Same(stale.Target, after) alone would pass if the
    /// reset simply stopped dropping anything.
    /// </summary>
    [SkippableFact]
    public void AfterReset_AHandleBoundBeforeIt_ReResolvesToTheNewInstance()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        BcRuntime.ResetSingleInstanceCache();
        var root = Root();

        // An AL variable that resolved in the previous test window and is still alive.
        var boundBefore = new NavCodeunitHandle(root, ProbeId);
        var before = Assert.IsType<Codeunit69001>(boundBefore.Target);
        before.Token = "BEFORE-RESET";

        BcRuntime.ResetSingleInstanceCache();

        // A callback's own local variable, resolving for the first time after the reset.
        var boundAfter = new NavCodeunitHandle(root, ProbeId);
        var after = Assert.IsType<Codeunit69001>(boundAfter.Target);

        Assert.NotSame(before, after);
        Assert.Equal(string.Empty, after.Token);

        // The invariant: the surviving handle must now see what everyone else sees.
        Assert.Same(after, boundBefore.Target);

        // And state written through the surviving handle after the reset must be visible
        // through the freshly bound one — the exact read/write pairing #2143 reported as
        // broken (test method writes, callback reads, same test).
        ((Codeunit69001)boundBefore.Target).Token = "AFTER-RESET";
        Assert.Equal("AFTER-RESET", ((Codeunit69001)boundAfter.Target).Token);
    }

    /// <summary>
    /// Negative: the reset must still be a real boundary. A fix that kept every handle
    /// pointing at one immortal instance would satisfy the test above and silently leak
    /// SingleInstance state from one test into the next, which is what
    /// ResetSingleInstanceCache exists to prevent.
    /// </summary>
    [SkippableFact]
    public void AfterReset_StateWrittenBeforeIt_IsNotVisible()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        BcRuntime.ResetSingleInstanceCache();
        var root = Root();

        var first = new NavCodeunitHandle(root, ProbeId);
        ((Codeunit69001)first.Target).Token = "LEAKED";

        BcRuntime.ResetSingleInstanceCache();

        var second = new NavCodeunitHandle(root, ProbeId);
        Assert.Equal(string.Empty, ((Codeunit69001)second.Target).Token);
    }
}
