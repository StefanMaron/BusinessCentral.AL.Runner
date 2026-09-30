// #5004: an event raised by an extension whose base object cannot be named must refuse, never
// return as though nothing subscribed.
using AlRunner;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

// Reads the RecordPatches parse statics through ExtensionBaseObjectIds.
[Collection(RecordPatchesSerialCollection.Name)]
public class ExtensionPublisherResolutionTests
{
    [Theory]
    [InlineData(BcRuntime.ExtensionKindTable)]
    [InlineData(BcRuntime.ExtensionKindPage)]
    [InlineData(BcRuntime.ExtensionKindReport)]
    public void UnresolvableBase_RefusesNamingTheExtensionAndTheEvent(string extensionKind)
    {
        // An instance with no ParentObject, and an id no source registry knows.
        var ex = Assert.Throws<RunnerOutOfScopeException>(() =>
            BcRuntime.ResolveExtensionPublisherOrThrow(extensionKind, 979797, "OnUnresolvedProbe", new object()));

        Assert.Equal($"{extensionKind} 979797 event OnUnresolvedProbe", ex.Api);
        Assert.Contains("could not be resolved", ex.Reason);
    }

    [Fact]
    public void NullInstanceAndUnknownId_Refuses()
        => Assert.Throws<RunnerOutOfScopeException>(() =>
            BcRuntime.ResolveExtensionPublisherOrThrow(BcRuntime.ExtensionKindTable, 979798, "OnNoInstance", null));
}
