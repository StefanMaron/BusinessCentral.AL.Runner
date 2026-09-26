// Pins BcRuntime.ReleaseLocalTestPageReferences (MethodScopePatches.cs), the runner half of
// #4732: a disposing method scope releases the page reference held by each of its own LOCAL
// TestPage handles, so BC's own reference counting disposes the NavTestPage and its Dispose
// removes an unconsumed Trap(). The BC-behaviour half (a trap set on a local TestPage ends with
// the variable) is pinned upstream in the corpus; see the PR body for the corpus PR.
using AlRunner;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

// Constructs Ncl tree objects under BcRuntime.RootTreeStub, so it shares the serial bc-engine
// collection. See BcEngineCollection.cs.
[Collection(BcEngineCollection.Name)]
public class ReleaseLocalTestPageReferencesTests
{
    // Outside every idRange this repo declares; the handle never resolves it.
    private const int PageId = 69004;

    private readonly BcEngineFixture _engine;

    public ReleaseLocalTestPageReferencesTests(BcEngineFixture engine) => _engine = engine;

    private ITreeObject Root()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        var root = BcRuntime.RootTreeStub;
        Assert.True(root != null, "BcRuntime.RootTreeStub is null after the engine bootstrap.");
        return root!;
    }

    // Stands in for the NavTestPage: any tree object can be a reference target, and HasTarget
    // reads the reference without casting it, so the test needs no page metadata. Parented on
    // its owning handle, as BcRuntime.NavTestPageHandle_CreateTarget parents the real page;
    // BC's TreeHandler disposes a released target only when the releasing handle is its parent.
    private static NavScope Target(ITreeObject owner) => new NavScope(owner);

    private static void Reference(NavTestPageHandle handle, ITreeObject target)
        => handle.Tree.SetReferenceTarget(target);

    [SkippableFact]
    public void LocalHandle_WithTheOnlyReference_IsReleased_AndTheTargetDisposed()
    {
        var root = Root();
        var scope = new NavScope(root);
        var handle = new NavTestPageHandle(scope, PageId);
        var target = Target(handle);
        Reference(handle, target);
        Assert.True(handle.HasTarget);
        Assert.False(target.Tree.IsDisposed);

        BcRuntime.ReleaseLocalTestPageReferences(scope);

        Assert.False(handle.HasTarget,
            "a disposing scope's own TestPage handle must release its page reference");
        Assert.True(target.Tree.IsDisposed,
            "the last reference released must dispose the page — that Dispose is what removes BC's trap");
    }

    [SkippableFact]
    public void LocalHandle_ReferencingAPageOwnedElsewhere_ReleasesOnlyItsOwnReference()
    {
        // A page owned by a handle outside the scope and merely referenced by the local one
        // must survive the local handle's release.
        var root = Root();
        var scope = new NavScope(root);
        var local = new NavTestPageHandle(scope, PageId);
        var outside = new NavTestPageHandle(root, PageId);
        var target = Target(outside);
        Reference(local, target);
        Reference(outside, target);

        BcRuntime.ReleaseLocalTestPageReferences(scope);

        Assert.False(local.HasTarget);
        Assert.True(outside.HasTarget);
        Assert.False(target.Tree.IsDisposed,
            "a page still referenced outside the disposing scope must not be disposed");

        outside.ClearReference();
    }

    [SkippableFact]
    public void HandleNotParentedOnTheDisposingScope_KeepsItsPage()
    {
        var root = Root();
        var scope = new NavScope(root);
        var elsewhere = new NavTestPageHandle(root, PageId);
        var target = Target(elsewhere);
        Reference(elsewhere, target);

        BcRuntime.ReleaseLocalTestPageReferences(scope);

        Assert.True(elsewhere.HasTarget);
        Assert.False(target.Tree.IsDisposed);

        elsewhere.ClearReference();
    }

    [Fact]
    public void NullScope_DoesNotThrow()
        => Assert.Null(Record.Exception(() => BcRuntime.ReleaseLocalTestPageReferences(null)));
}
