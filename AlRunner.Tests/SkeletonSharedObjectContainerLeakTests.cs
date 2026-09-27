// SkeletonSharedObjectContainerLeakTests — proves that the process-wide skeleton
// TreeSharedObjectContainer's child chain no longer grows without bound.
//
// Root cause (see NavRecordRefPatches.cs / RecordPatches.cs comments)
// ---------------------------------------------------------------------
// TreeObject's ctor unconditionally links every new instance into its parent
// TreeHandler's child linked list (TreeHandler.CreateTreeHandler -> InternalAddChild).
// AlRunner.BcRuntime._skeletonSharedObjectContainer is a single process-wide static
// TreeSharedObjectContainer that every SharedRecordRef / SharedNavStream / etc. wrapper
// is parented to. Nothing was ever unlinking those children, so the chain — and every
// live NavRecord each child transitively holds — grew for the life of the process.
//
// The fix sweeps that chain via BcRuntime.DisposeSkeletonSharedObjectContainerChildren()
// at the same per-test boundary RecordPatches.ResetPerTestState() already resets
// _dataAccessByTable at.
//
// This test builds an ISOLATED root + container (independent of the real process-wide
// BcRuntime.RootTreeStub) and points BcRuntime's private _skeletonSharedObjectContainer
// field at it via reflection, so the real ResetPerTestState() codepath — unmodified —
// operates on our throwaway container. It then runs many "create N wrappers, then
// ResetPerTestState()" cycles and asserts the container's child count is bounded
// afterwards, rather than growing linearly with cycle count.
//
// RED/GREEN proof (see PR/verification notes): commenting out the
// `AlRunner.BcRuntime.DisposeSkeletonSharedObjectContainerChildren();` call inside
// RecordPatches.ResetPerTestState() reproduces the leak — this test then fails with the
// child count equal to cycles * perCycle (100 for the constants below) instead of the
// asserted bound.

using System.Reflection;
using AlRunner;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

// Loads Ncl types in-process, so it must share the serial bc-engine collection with the
// class that Cecil-rewrites Ncl.dll on disk — otherwise it maps a half-written image.
// See BcEngineCollection.cs.
[Collection(BcEngineCollection.Name)]
public class SkeletonSharedObjectContainerLeakTests
{
    private const int Cycles = 20;
    private const int PerCycle = 5;

    private readonly BcEngineFixture _engine;

    public SkeletonSharedObjectContainerLeakTests(BcEngineFixture engine) => _engine = engine;

    [SkippableFact]
    public void ResetPerTestState_SweepsSkeletonContainerChildren_AcrossManyCycles()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var nclAssembly = typeof(ITreeObject).Assembly;

        // Isolated root — deliberately NOT BcRuntime.RootTreeStub, so this test does not
        // depend on (or pollute) the real process-wide skeleton bootstrap.
        var root = new RootTreeObject();
        var container = new TreeSharedObjectContainer(root);

        var tShared = nclAssembly.GetType("Microsoft.Dynamics.Nav.Runtime.SharedRecordRef")!;
        var tIContainer = nclAssembly.GetType("Microsoft.Dynamics.Nav.Runtime.ITreeSharedObjectContainer")!;
        var sharedCtor = tShared.GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { tIContainer }, null)!;

        var containerField = typeof(BcRuntime).GetField(
            "_skeletonSharedObjectContainer", BindingFlags.NonPublic | BindingFlags.Static)!;
        var originalContainer = containerField.GetValue(null);
        try
        {
            // Point the real static field at our isolated container so the unmodified
            // BcRuntime.DisposeSkeletonSharedObjectContainerChildren() (invoked internally by
            // RecordPatches.ResetPerTestState()) operates on it.
            containerField.SetValue(null, container);

            int countAfterLastReset = -1;
            for (int cycle = 0; cycle < Cycles; cycle++)
            {
                for (int i = 0; i < PerCycle; i++)
                    sharedCtor.Invoke(new object?[] { container });

                RecordPatches.ResetPerTestState();
                countAfterLastReset = ((ITreeObject)container).Tree.Children.Count;
            }

            var totalCreated = Cycles * PerCycle;
            Assert.True(countAfterLastReset <= 2,
                $"Expected the skeleton shared-object container's child chain to be swept " +
                $"back to a bounded count (<=2) by ResetPerTestState() after every cycle, but " +
                $"{countAfterLastReset} children remained after {Cycles} cycles x {PerCycle} " +
                $"wrappers/cycle ({totalCreated} created total). A count anywhere near " +
                $"{totalCreated} means the sweep is not running and the leak is back.");
        }
        finally
        {
            containerField.SetValue(null, originalContainer);
        }
    }

    /// <summary>
    /// #4781: with a SingleInstance instance cached — the normal state of a Base App run — the
    /// sweep takes its per-child branch. It must still dispose every wrapper nothing reaches, and
    /// keep only the one the cached instance reaches (here through a RecordRef it holds).
    /// </summary>
    [SkippableFact]
    public void ResetPerTestState_WithASingleInstanceCached_SweepsUnreachableChildren_AndKeepsTheReachableOne()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var nclAssembly = typeof(ITreeObject).Assembly;
        var root = new RootTreeObject();
        var container = new TreeSharedObjectContainer(root);
        var tShared = nclAssembly.GetType("Microsoft.Dynamics.Nav.Runtime.SharedRecordRef")!;
        var tIContainer = nclAssembly.GetType("Microsoft.Dynamics.Nav.Runtime.ITreeSharedObjectContainer")!;
        var sharedCtor = tShared.GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { tIContainer }, null)!;

        var containerField = typeof(BcRuntime).GetField(
            "_skeletonSharedObjectContainer", BindingFlags.NonPublic | BindingFlags.Static)!;
        var cache = (System.Collections.IDictionary)typeof(BcRuntime).GetField(
            "_singleInstanceCache", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var originalContainer = containerField.GetValue(null);
        var instance = new Codeunit69006(root);
        try
        {
            containerField.SetValue(null, container);
            cache[Codeunit69006.Id] = instance;

            var unreachable = Enumerable.Range(0, PerCycle)
                .Select(_ => (ITreeObject)sharedCtor.Invoke(new object?[] { container })).ToList();
            var reachable = (ITreeObject)sharedCtor.Invoke(new object?[] { container });
            // What a RecordRef global does: a reference child of the instance, targeting the wrapper.
            var recordRef = new NavRecordRef(instance);
            ((ITreeObject)recordRef).Tree.SetReferenceTarget(reachable);
            Assert.Equal(PerCycle + 1, ((ITreeObject)container).Tree.Children.Count);

            RecordPatches.ResetPerTestState();

            var left = ((ITreeObject)container).Tree.Children;
            Assert.True(left.Count == 1 && ReferenceEquals(left[0], reachable),
                $"expected only the wrapper the cached SingleInstance instance reaches to survive the "
                + $"sweep, but {left.Count} child(ren) remain. More than one means the per-child "
                + "branch no longer disposes what nothing reaches (the leak is back for every run "
                + "with a SingleInstance instance cached); zero means it disposed the reachable one.");
            Assert.All(unreachable, u => Assert.True(u.Tree == null || u.Tree.IsDisposed,
                "an unreachable wrapper was not disposed by the sweep."));
        }
        finally
        {
            cache.Remove(Codeunit69006.Id);
            containerField.SetValue(null, originalContainer);
        }
    }

    [SkippableFact]
    public void WithoutReset_SkeletonContainerChildren_GrowLinearlyWithCyclesAsBaseline()
    {
        // Sanity companion to the test above: proves the harness itself is capable of
        // observing growth (i.e. it isn't accidentally bounded for some unrelated reason,
        // such as the container silently refusing to parent children). Never calls
        // ResetPerTestState(), so the child count must equal everything created.
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var nclAssembly = typeof(ITreeObject).Assembly;
        var root = new RootTreeObject();
        var container = new TreeSharedObjectContainer(root);

        var tShared = nclAssembly.GetType("Microsoft.Dynamics.Nav.Runtime.SharedRecordRef")!;
        var tIContainer = nclAssembly.GetType("Microsoft.Dynamics.Nav.Runtime.ITreeSharedObjectContainer")!;
        var sharedCtor = tShared.GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { tIContainer }, null)!;

        const int totalToCreate = 17;
        for (int i = 0; i < totalToCreate; i++)
            sharedCtor.Invoke(new object?[] { container });

        var count = ((ITreeObject)container).Tree.Children.Count;
        Assert.Equal(totalToCreate, count);
    }
}

/// <summary>A stand-in SingleInstance codeunit for the sweep test above. Named
/// <c>Codeunit{id}</c> like Codeunit69001/69002, with an id outside every AL idRange here.</summary>
internal sealed class Codeunit69006 : NavCodeunit
{
    internal const int Id = 69006;
    public Codeunit69006(ITreeObject parent) : base(parent, Id) { }
}
