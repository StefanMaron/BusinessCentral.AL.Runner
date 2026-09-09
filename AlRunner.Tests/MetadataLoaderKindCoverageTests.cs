// MetadataLoaderKindCoverageTests — #3599: RunnerXmlMetadataLoader.GetMetaObjectXmlMetadata
// served four kinds (Report, Page, XmlPort, Table) and threw RunnerOutOfScopeException for
// everything else, even when AlObjectMetadataRegistry (#3548) already held BC's own emitted
// document for that exact (kind, id). This is a RUNNER-MECHANISM test: it calls the seam
// directly, the same way BC's own MetaObjectCache / NCLObjectMetadataLoaderExtensions.GetMeta*
// helpers call it (verified via bc-decompiler: NCLObjectMetadataLoaderExtensions.GetMetaCodeunit/
// GetMetaQuery/GetMetaEnum all funnel through RetrieveRuntimeObject ->
// loader.GetMetaObjectXmlMetadata(new ApplicationObjectId(objectType, id), appGroup)), without
// needing the BC engine, a real bundle, or a subprocess spawn.
//
// ObjectType (Microsoft.Dynamics.Nav.Types.ObjectType) and the AL compiler's SymbolKind
// (the string AlObjectMetadataRegistry is keyed by) are two different enums; RegistryKindByObjectType
// in RunnerXmlMetadataLoader.cs carries the measured mapping between them. The one divergence:
// ObjectType.CodeUnit vs SymbolKind's "Codeunit" — covered by CodeUnit_MapsToCodeunitRegistryKind
// below so a future rename of either enum fails loudly here instead of silently losing the kind.
using System.Xml;
using AlRunner;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Types;
using Xunit;

namespace AlRunner.Tests;

public sealed class MetadataLoaderKindCoverageTests : IDisposable
{
    public MetadataLoaderKindCoverageTests() => AlObjectMetadataRegistry.Clear();
    public void Dispose() => AlObjectMetadataRegistry.Clear();

    private static readonly RunnerXmlMetadataLoader Loader = new();

    /// <summary>Every (ObjectType, registry kind) pair the fallback is supposed to serve,
    /// excluding Report/Page/Table/XmlPort — those already have their own branches and their
    /// own tests (TableMetadataFromBcDocumentTests and the report/page/xmlport registries).</summary>
    public static IEnumerable<object[]> NewlyServedKinds()
    {
        yield return new object[] { ObjectType.CodeUnit, "Codeunit" };
        yield return new object[] { ObjectType.Query, "Query" };
        yield return new object[] { ObjectType.Enum, "Enum" };
        yield return new object[] { ObjectType.EnumExtension, "EnumExtension" };
        yield return new object[] { ObjectType.PermissionSet, "PermissionSet" };
        yield return new object[] { ObjectType.PermissionSetExtension, "PermissionSetExtension" };
        yield return new object[] { ObjectType.TableExtension, "TableExtension" };
        yield return new object[] { ObjectType.PageExtension, "PageExtension" };
        yield return new object[] { ObjectType.ReportExtension, "ReportExtension" };
    }

    // RED (pre-fix): every one of these ObjectTypes hit the final `throw` in
    // GetMetaObjectXmlMetadata even though AlObjectMetadataRegistry held a document for the
    // exact (kind, id) — confirmed by running this assembly against the pre-fix loader, where
    // this test failed with RunnerOutOfScopeException rather than reaching the Assert.
    [Theory]
    [MemberData(nameof(NewlyServedKinds))]
    public void PreviouslyThrowingKind_NowAnswersTheRegisteredBcDocument(ObjectType objectType, string registryKind)
    {
        const int id = 88010;
        const string name = "MLKC Thing";
        var xml = $"<{registryKind} Id=\"{id}\" Name=\"{name}\"><Marker>{registryKind}-{id}-payload</Marker></{registryKind}>";
        AlObjectMetadataRegistry.Register(registryKind, id, name, xml);

        var result = Loader.GetMetaObjectXmlMetadata(new ApplicationObjectId(objectType, id), appGroup: null!);

        Assert.NotNull(result.Document.DocumentElement);
        // A concrete value out of the registered document, not merely "did not throw": the
        // marker text is unique per (kind, id) so a wrong-kind or wrong-id lookup would fail
        // this assertion even though it returned SOME document.
        Assert.Equal($"{registryKind}-{id}-payload", result.Document.DocumentElement!.SelectSingleNode("Marker")!.InnerText);
    }

    // Negative direction: an id the registry never saw for that kind still throws loudly —
    // the fallback must never manufacture an empty/default document (loud-failures rule).
    [Theory]
    [MemberData(nameof(NewlyServedKinds))]
    public void UnregisteredId_StillThrowsRunnerOutOfScopeException(ObjectType objectType, string registryKind)
    {
        var ex = Assert.Throws<RunnerOutOfScopeException>(
            () => Loader.GetMetaObjectXmlMetadata(new ApplicationObjectId(objectType, 88099), appGroup: null!));
        Assert.Contains(objectType.ToString(), ex.Message);
    }

    // A kind the registry has never been taught about at all (per docs/object-metadata-capture.md,
    // Profile never reaches AddApplicationObject with a non-empty document) must still refuse
    // rather than silently answering something for it.
    [Fact]
    public void KindWithNoRegistryMapping_StillThrows()
    {
        AlObjectMetadataRegistry.Register("Profile", null, "MLKC Profile", "<Profile/>");

        Assert.Throws<RunnerOutOfScopeException>(
            () => Loader.GetMetaObjectXmlMetadata(new ApplicationObjectId(ObjectType.Profile, 0), appGroup: null!));
    }

    // The one spelling divergence between the two enums this mapping bridges, pinned so a
    // future rename of either ObjectType.CodeUnit or SymbolKind.Codeunit is caught here rather
    // than silently losing codeunit metadata to the fallback's final throw.
    [Fact]
    public void CodeUnit_MapsToCodeunitRegistryKind()
    {
        const int id = 88011;
        AlObjectMetadataRegistry.Register("Codeunit", id, "MLKC Cu", "<Codeunit Id=\"88011\"><Marker>cu-marker</Marker></Codeunit>");

        var result = Loader.GetMetaObjectXmlMetadata(new ApplicationObjectId(ObjectType.CodeUnit, id), appGroup: null!);

        Assert.Equal("cu-marker", result.Document.DocumentElement!.SelectSingleNode("Marker")!.InnerText);
    }

    // Table keeps going through its own #3552 branch (RecordPatches.BcTableMetadataKind), not
    // through the new fallback dictionary — this pins that the two routes do not collide, since
    // both ultimately read the same registry.
    [Fact]
    public void Table_StillServedByItsOwnBranch_NotTheFallback()
    {
        const int id = 88012;
        AlObjectMetadataRegistry.Register(RecordPatches.BcTableMetadataKind, id, "MLKC Table",
            "<Table Id=\"88012\"><Marker>table-marker</Marker></Table>");

        var result = Loader.GetMetaObjectXmlMetadata(new ApplicationObjectId(ObjectType.Table, id), appGroup: null!);

        Assert.Equal("table-marker", result.Document.DocumentElement!.SelectSingleNode("Marker")!.InnerText);
    }
}
