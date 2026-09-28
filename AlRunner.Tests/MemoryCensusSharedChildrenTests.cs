using System.Reflection;
using AlRunner;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

/// <summary>#4812: the mem-census <c>sharedChildren</c> column measures the skeleton
/// shared-object container's real child count.</summary>
[Collection(BcEngineCollection.Name)]
public class MemoryCensusSharedChildrenTests
{
    private readonly BcEngineFixture _engine;

    public MemoryCensusSharedChildrenTests(BcEngineFixture engine) => _engine = engine;

    private static readonly FieldInfo ContainerField = typeof(BcRuntime).GetField(
        "_skeletonSharedObjectContainer", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void AddSharedChildren(TreeSharedObjectContainer container, int count)
    {
        var ncl = typeof(ITreeObject).Assembly;
        var tShared = ncl.GetType("Microsoft.Dynamics.Nav.Runtime.SharedRecordRef")!;
        var tIContainer = ncl.GetType("Microsoft.Dynamics.Nav.Runtime.ITreeSharedObjectContainer")!;
        var ctor = tShared.GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { tIContainer }, null)!;
        for (var i = 0; i < count; i++)
            ctor.Invoke(new object?[] { container });
    }

    private T WithContainer<T>(object? container, Func<T> read)
    {
        var original = ContainerField.GetValue(null);
        try
        {
            ContainerField.SetValue(null, container);
            return read();
        }
        finally
        {
            ContainerField.SetValue(null, original);
        }
    }

    [SkippableFact]
    public void SharedChildren_ReportsTheContainersLiveChildCount()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        var container = new TreeSharedObjectContainer(new RootTreeObject());
        AddSharedChildren(container, 3);

        Assert.Equal("3", WithContainer(container, BcRuntime.CensusSharedObjectContainerChildCount));
    }

    [SkippableFact]
    public void SharedChildren_FollowsTheSweep_DownToZero()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        var container = new TreeSharedObjectContainer(new RootTreeObject());
        AddSharedChildren(container, 4);

        var (before, after) = WithContainer(container, () =>
        {
            var b = BcRuntime.CensusSharedObjectContainerChildCount();
            ((ITreeObject)container).Tree.DisposeAllChildren();
            return (b, BcRuntime.CensusSharedObjectContainerChildCount());
        });

        Assert.Equal("4", before);
        Assert.Equal("0", after);
    }

    /// <summary>No RecordRef has created the container yet: nothing can be parented to it, so
    /// zero is a measurement, not a fallback.</summary>
    [Fact]
    public void SharedChildren_NoContainerYet_IsZero()
    {
        Assert.Equal("0", BcRuntime.CensusTreeChildCount(null));
    }

    [SkippableFact]
    public void SharedChildren_DisposedContainer_SaysUnavailable_NotANumber()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        var container = new TreeSharedObjectContainer(new RootTreeObject());
        AddSharedChildren(container, 2);
        ((ITreeObject)container).Tree.Dispose();

        var reported = BcRuntime.CensusTreeChildCount(container);

        Assert.StartsWith("unavailable(", reported);
        Assert.Contains("disposed", reported);
    }

    [Fact]
    public void SharedChildren_NotATreeObject_SaysUnavailable_NotANumber()
    {
        var reported = BcRuntime.CensusTreeChildCount(new object());

        Assert.Equal("unavailable(Object is not an ITreeObject)", reported);
    }

    [Fact]
    public void RssMB_Unreadable_SaysUnavailable_AndReadable_IsMegabytes()
    {
        Assert.StartsWith("unavailable(", MemoryCensus.RssMB(-1));
        Assert.Equal("2.0", MemoryCensus.RssMB(2048));
    }
}
