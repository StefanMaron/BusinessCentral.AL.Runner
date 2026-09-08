using System.IO;
using System.Linq;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #3548 — <see cref="AlObjectMetadataRegistry"/>'s own contract, without spawning the
/// runner: (kind, id) is the identity, not the id; an id-less kind is addressable by
/// name; and the sidecar round-trips every entry, because the sidecar is the only thing
/// standing between the capture and an empty registry on every warm run.
///
/// Serialized against the other tests that touch this static registry — see
/// <see cref="ObjectMetadataRegistrySerialCollection"/>.
/// </summary>
[Collection("object-metadata-registry")]
public class ObjectMetadataRegistryTests : IDisposable
{
    public ObjectMetadataRegistryTests() => AlObjectMetadataRegistry.Clear();
    public void Dispose() => AlObjectMetadataRegistry.Clear();

    [Fact]
    public void SameId_DifferentKinds_AreDifferentEntries()
    {
        AlObjectMetadataRegistry.Register("Table", 70660, "OMR Thing", "<MetaTable ID=\"70660\" />");
        AlObjectMetadataRegistry.Register("Page", 70660, "OMR Thing List", "<PageDefinition ID=\"70660\" />");

        Assert.Equal(2, AlObjectMetadataRegistry.Count);

        Assert.True(AlObjectMetadataRegistry.TryGet("Table", 70660, out var table));
        Assert.Equal("<MetaTable ID=\"70660\" />", table);

        Assert.True(AlObjectMetadataRegistry.TryGet("Page", 70660, out var page));
        Assert.Equal("<PageDefinition ID=\"70660\" />", page);

        // Negative direction: a kind that was never registered under this id must not
        // resolve to the table's or the page's document just because the id is known.
        Assert.False(AlObjectMetadataRegistry.TryGet("Codeunit", 70660, out var codeunit));
        Assert.Equal(string.Empty, codeunit);
        // And an id nothing registered stays unknown for a kind that IS known.
        Assert.False(AlObjectMetadataRegistry.TryGet("Table", 70661, out _));
    }

    [Fact]
    public void RegisteringTheSameIdentityTwice_ReplacesRatherThanDuplicates()
    {
        AlObjectMetadataRegistry.Register("Table", 70660, "OMR Thing", "<MetaTable Access=\"Internal\" />");
        AlObjectMetadataRegistry.Register("Table", 70660, "OMR Thing", "<MetaTable Access=\"Public\" />");

        Assert.Equal(1, AlObjectMetadataRegistry.Count);
        Assert.True(AlObjectMetadataRegistry.TryGet("Table", 70660, out var xml));
        Assert.Equal("<MetaTable Access=\"Public\" />", xml);
    }

    [Fact]
    public void IdlessKind_IsAddressableByName_AndAnEmptyDocumentIsRefused()
    {
        AlObjectMetadataRegistry.Register("Profile", null, "OMR Profile", "<Profile ProfileID=\"OMR Profile\" />");

        Assert.True(AlObjectMetadataRegistry.TryGetByName("Profile", "OMR Profile", out var xml));
        Assert.Equal("<Profile ProfileID=\"OMR Profile\" />", xml);
        Assert.False(AlObjectMetadataRegistry.TryGetByName("Profile", "Some Other Profile", out _));

        // A symbol arriving with no metadata document is not an entry with an empty
        // document — a consumer must be able to tell "BC said nothing" from "BC said
        // nothing about this object" (.claude/rules/loud-failures.md).
        AlObjectMetadataRegistry.Register("Table", 70662, "Empty", "");
        Assert.False(AlObjectMetadataRegistry.TryGet("Table", 70662, out _));
        // Neither is an identity-less registration.
        AlObjectMetadataRegistry.Register("Profile", null, "", "<Profile />");
        Assert.Equal(1, AlObjectMetadataRegistry.Count);
    }

    [Fact]
    public void Sidecar_RoundTripsEveryEntry_AndScopesToTheKeysAsked()
    {
        AlObjectMetadataRegistry.Register("Table", 70660, "OMR Thing", "<MetaTable ID=\"70660\" />");
        AlObjectMetadataRegistry.Register("Page", 70660, "OMR Thing List", "<PageDefinition ID=\"70660\" />");
        AlObjectMetadataRegistry.Register("Profile", null, "OMR Profile", "<Profile />");
        // A sibling app's entry, which this dependency's sidecar must NOT carry.
        AlObjectMetadataRegistry.Register("Table", 79999, "Foreign Thing", "<MetaTable ID=\"79999\" />");

        var dir = TestScratch.Dir("al-runner-objmeta-sidecar");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "dep.object-metadata.json");
        var mine = new[]
        {
            AlObjectMetadataRegistry.KeyFor("Table", 70660, "OMR Thing"),
            AlObjectMetadataRegistry.KeyFor("Page", 70660, "OMR Thing List"),
            AlObjectMetadataRegistry.KeyFor("Profile", null, "OMR Profile"),
        };
        Assert.Equal(3, AlObjectMetadataRegistry.SaveSidecar(path, mine));

        AlObjectMetadataRegistry.Clear();
        Assert.Equal(0, AlObjectMetadataRegistry.Count);

        Assert.Equal(3, AlObjectMetadataRegistry.LoadSidecar(path));
        Assert.True(AlObjectMetadataRegistry.TryGet("Table", 70660, out var table));
        Assert.Equal("<MetaTable ID=\"70660\" />", table);
        Assert.True(AlObjectMetadataRegistry.TryGet("Page", 70660, out var page));
        Assert.Equal("<PageDefinition ID=\"70660\" />", page);
        Assert.True(AlObjectMetadataRegistry.TryGetByName("Profile", "OMR Profile", out _));
        // The scoping is the claim: the sibling app's entry was never written.
        Assert.False(AlObjectMetadataRegistry.TryGet("Table", 79999, out _));

        // Names survive too — the dependency sidecar's own scoping diff keys on
        // (kind, id-or-name), so a lost name makes an id-less entry unaddressable.
        Assert.Equal("OMR Thing List", AlObjectMetadataRegistry.Snapshot()
            .Single(e => e.Kind == "Page" && e.Id == 70660).Name);
    }

    [Fact]
    public void Sidecar_CorruptJson_Throws_SoCallersCanFallThroughToAMiss()
    {
        var dir = TestScratch.Dir("al-runner-objmeta-sidecar-bad");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "bad.object-metadata.json");

        File.WriteAllText(path, "{ not json");
        Assert.ThrowsAny<Exception>(() => AlObjectMetadataRegistry.LoadSidecar(path));

        // Well-formed JSON without the array is just as unusable, and silently replaying
        // zero entries is the failure this whole registry exists to avoid.
        File.WriteAllText(path, "{ \"somethingElse\": [] }");
        Assert.Throws<InvalidDataException>(() => AlObjectMetadataRegistry.LoadSidecar(path));

        Assert.Equal(0, AlObjectMetadataRegistry.Count);
    }
}

[CollectionDefinition("object-metadata-registry", DisableParallelization = true)]
public class ObjectMetadataRegistrySerialCollection { }
