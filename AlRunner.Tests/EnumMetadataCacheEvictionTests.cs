// #3577: a registry write for an enum id drops the NCL metadata already built for that id, and
// only that id, so the next NCLEnumMetadata_CreateByIdAlAware reads the changed registration.
using AlRunner;
using Xunit;
using NCLOptionMetadata = Microsoft.Dynamics.Nav.Runtime.NCLOptionMetadata;

namespace AlRunner.Tests;

[Collection(EnumMetadataRegistrySerialCollection.Name)]
public sealed class EnumMetadataCacheEvictionTests : IDisposable
{
    // Inside no app.json idRange that ships in this repo; the registry is process-global.
    private const int EnumId = 94945;
    private const int OtherEnumId = 94946;

    public EnumMetadataCacheEvictionTests() => AlEnumMetadataRegistry.Clear();

    public void Dispose() => AlEnumMetadataRegistry.Clear();

    private static void RegisterBase(int id, string caption)
        => AlEnumMetadataRegistry.Register(id, "Cache Gap State", new[] { "Base" }, new[] { 0 }, captions: new[] { caption });

    [Fact]
    public void RegisterExtension_AfterMaterialising_RebuildsWithTheExtensionValue()
    {
        RegisterBase(EnumId, "Base caption");
        var before = BcRuntime.NCLEnumMetadata_CreateByIdAlAware(EnumId);
        Assert.Equal(new[] { 0 }, before.GetOrdinals().ToArray());

        var version = AlEnumMetadataRegistry.Version;
        AlEnumMetadataRegistry.RegisterExtension(EnumId, "Cache Gap Extension", new[] { "Added" }, new[] { 10 },
            captions: new[] { "Added caption" });
        var after = BcRuntime.NCLEnumMetadata_CreateByIdAlAware(EnumId);

        // The write that evicts also moves the version a memo over the registry keys on (#4872).
        Assert.True(AlEnumMetadataRegistry.Version > version);
        Assert.NotSame(before, after);
        Assert.Equal(new[] { 0, 10 }, after.GetOrdinals().ToArray());
        Assert.Equal("Base caption", after.GetCaptionFromIndex(0));
        Assert.Equal("Added", after.GetOptionFromIndex(10));
        Assert.Equal("Added caption", after.GetCaptionFromIndex(10));
    }

    [Fact]
    public void ReplacingTheBaseRegistration_AfterMaterialising_RebuildsFromTheNewEntry()
    {
        RegisterBase(EnumId, "Base caption");
        var before = BcRuntime.NCLEnumMetadata_CreateByIdAlAware(EnumId);
        Assert.Equal("Base caption", before.GetCaptionFromIndex(0));

        var version = AlEnumMetadataRegistry.Version;
        RegisterBase(EnumId, "Replaced caption");
        var after = BcRuntime.NCLEnumMetadata_CreateByIdAlAware(EnumId);

        Assert.True(AlEnumMetadataRegistry.Version > version);
        Assert.NotSame(before, after);
        Assert.Equal("Replaced caption", after.GetCaptionFromIndex(0));
    }

    [Fact]
    public void MutatingOneEnum_LeavesAnotherEnumsBuiltMetadataInPlace()
    {
        RegisterBase(EnumId, "Base caption");
        RegisterBase(OtherEnumId, "Other caption");
        var other = BcRuntime.NCLEnumMetadata_CreateByIdAlAware(OtherEnumId);
        var mine = BcRuntime.NCLEnumMetadata_CreateByIdAlAware(EnumId);

        AlEnumMetadataRegistry.RegisterExtension(EnumId, "Cache Gap Extension", new[] { "Added" }, new[] { 10 });
        RegisterBase(EnumId, "Replaced caption");

        Assert.NotSame(mine, BcRuntime.NCLEnumMetadata_CreateByIdAlAware(EnumId));
        Assert.Same(other, BcRuntime.NCLEnumMetadata_CreateByIdAlAware(OtherEnumId));
    }

    [Fact]
    public void ClearingTheRegistry_DropsBuiltMetadata_SoAnUnregisteredIdAnswersDefault()
    {
        RegisterBase(EnumId, "Base caption");
        var before = BcRuntime.NCLEnumMetadata_CreateByIdAlAware(EnumId);
        Assert.NotSame(NCLOptionMetadata.Default, before);

        AlEnumMetadataRegistry.Clear();

        Assert.Same(NCLOptionMetadata.Default, BcRuntime.NCLEnumMetadata_CreateByIdAlAware(EnumId));
    }
}
