// Pins the runner's SingleInstance bookkeeping against two leaks (#2181, #2185). Neither is
// AL-observable — both are counts of runner-owned objects — so they are pinned here rather
// than in the corpus. Probe codeunit: Codeunit69001 in SingleInstanceResetHandleInvalidationTests.cs.

using System.Reflection;
using AlRunner;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public class SingleInstanceResetLeakTests
{
    private const int ProbeId = 69001;
    private const int Cycles = 20;

    private readonly BcEngineFixture _engine;

    public SingleInstanceResetLeakTests(BcEngineFixture engine) => _engine = engine;

    private ITreeObject Session()
    {
        var session = BcRuntime.SkeletonSession as ITreeObject;
        Assert.True(session != null,
            "BcRuntime.SkeletonSession is null after the engine bootstrap — the cached instance " +
            "is only session-rooted when a session exists, so this test would measure nothing.");
        return session!;
    }

    private static ITreeObject Root()
    {
        var root = BcRuntime.RootTreeStub;
        Assert.True(root != null, "BcRuntime.RootTreeStub is null after the engine bootstrap.");
        return root!;
    }

    /// <summary>#2181: every reset used to leave the instance AND its keep-alive handle linked
    /// into the session tree — exactly two children per cycle, forever.</summary>
    [SkippableFact]
    public void Reset_UnlinksTheCachedInstanceAndItsKeepAliveHandle_FromTheSessionTree()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        BcRuntime.ResetSingleInstanceCache();
        var sessionTree = Session().Tree;
        var root = Root();
        int before = sessionTree.Children.Count;

        Codeunit69001? previous = null;
        // Left alive through every reset, so the bounded count below measures the reset and
        // not this test's own cleanup. They are parented on the root, not the session.
        var handles = new List<NavCodeunitHandle>();
        for (int cycle = 0; cycle < Cycles; cycle++)
        {
            // An AL variable that resolves the codeunit and is still alive at the reset.
            var handle = new NavCodeunitHandle(root, ProbeId);
            var instance = Assert.IsType<Codeunit69001>(handle.Target);
            Assert.NotSame(previous, instance);

            // Resolving it must have linked something into the session (the harness can see
            // growth), otherwise the bounded count below proves nothing.
            Assert.True(sessionTree.Children.Count > before,
                $"cycle {cycle}: resolving a SingleInstance codeunit added nothing to the session " +
                "tree, so this test cannot observe the leak it exists to catch.");

            BcRuntime.ResetSingleInstanceCache();

            int now = sessionTree.Children.Count;
            Assert.True(now == before,
                $"cycle {cycle}: session tree children before={before} after reset={now} " +
                $"delta={now - before}. A delta of {2 * (cycle + 1)} is the #2181 leak " +
                "(instance + keep-alive handle per reset).");

            // The reset invalidated the only AL handle holding it, so BC's refcount reaches
            // zero and disposes it.
            Assert.True(((ITreeObject)instance).Tree.IsDisposed,
                $"cycle {cycle}: the instance the reset dropped is still alive.");
            handles.Add(handle);
            previous = instance;
        }

        int after = sessionTree.Children.Count;
        foreach (var h in handles) h.Dispose();
        Assert.True(after == before,
            $"session tree children: before={before} after {Cycles} cycles={after} " +
            $"delta={after - before}. A delta of {2 * Cycles} is the #2181 leak " +
            "(instance + keep-alive handle per reset).");
    }

    /// <summary>The hazard #2181's fix must respect: a handle that holds the instance without
    /// ever going through CreateTarget (CloneReference / ALAssign / ALByValue build handles this
    /// way) is not tracked, so the reset cannot invalidate it. It must keep a LIVE instance —
    /// never a disposed one, which is the NRE the keep-alive handle exists to prevent.</summary>
    [SkippableFact]
    public void Reset_WhileAnUntrackedHandleHoldsTheInstance_LeavesItAliveUntilThatHandleGoes()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        BcRuntime.ResetSingleInstanceCache();
        var sessionTree = Session().Tree;
        var root = Root();
        int before = sessionTree.Children.Count;

        var bound = new NavCodeunitHandle(root, ProbeId);
        var instance = Assert.IsType<Codeunit69001>(bound.Target);
        instance.Token = "HELD";

        // The safety of disposing only the keep-alive handle rests on the instance being
        // refcounted (TreeSharedObjectHandler); a Normal tree object never counts references.
        Assert.Equal(TreeObjectType.Shared, ((ITreeObject)instance).Type);

        // NavCodeunitHandle(ITreeObject, NavCodeunit): assigns Target directly, bypassing
        // CreateTarget — the same shape as a copied handle.
        var untracked = new NavCodeunitHandle(root, instance);

        BcRuntime.ResetSingleInstanceCache();

        Assert.False(((ITreeObject)instance).Tree.IsDisposed,
            "the reset disposed an instance an untracked handle still references.");
        var stillHeld = Assert.IsType<Codeunit69001>(untracked.Target);
        Assert.Same(instance, stillHeld);
        Assert.Equal("HELD", stillHeld.Token);

        // The tracked handle still re-resolves to a fresh instance (#2143's invariant).
        Assert.NotSame(instance, bound.Target);

        // Once the last reference goes, BC's own refcount disposes and unlinks it.
        untracked.Dispose();
        Assert.True(((ITreeObject)instance).Tree.IsDisposed,
            "the instance survived its last reference being dropped.");

        bound.Dispose();
        BcRuntime.ResetSingleInstanceCache();
        Assert.Equal(before, sessionTree.Children.Count);
    }

    private static int BoundHandleCount()
    {
        var field = typeof(BcRuntime).GetField("_singleInstanceBoundHandles",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(field != null, "BcRuntime._singleInstanceBoundHandles not found — renamed?");
        var list = (System.Collections.ICollection)field!.GetValue(null)!;
        return list.Count;
    }

    /// <summary>#2185: with no reset (--isolation disabled) every resolution appended an entry
    /// that was never pruned.</summary>
    [SkippableFact]
    public void WithoutReset_BoundHandleList_StaysBoundedAsHandlesAreDisposed()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        BcRuntime.ResetSingleInstanceCache();
        var root = Root();

        // A live handle bound before the churn: pruning must never drop it.
        var survivor = new NavCodeunitHandle(root, ProbeId);
        var first = Assert.IsType<Codeunit69001>(survivor.Target);

        const int churn = 2000;
        for (int i = 0; i < churn; i++)
        {
            var h = new NavCodeunitHandle(root, ProbeId);
            Assert.Same(first, h.Target);
            h.Dispose();
        }

        int count = BoundHandleCount();
        Assert.True(count <= 256,
            $"_singleInstanceBoundHandles holds {count} entries after {churn} resolve+dispose " +
            $"cycles with no reset; about {churn + 1} is the #2185 leak.");

        // Pruning kept the live entry: a reset still makes the survivor re-resolve.
        BcRuntime.ResetSingleInstanceCache();
        var fresh = new NavCodeunitHandle(root, ProbeId);
        var second = Assert.IsType<Codeunit69001>(fresh.Target);
        Assert.NotSame(first, second);
        Assert.Same(second, survivor.Target);

        survivor.Dispose();
        fresh.Dispose();
        BcRuntime.ResetSingleInstanceCache();
    }
}
