// AllObjInventoryStampTests — issue #4851 review. The AllObj row memo and each store's
// "already filled" set are keyed on RecordPatches.CurrentObjectInventoryStamp(), so an input that
// grows WITHIN one bundle reaches AllObj only if the stamp moves when it grows.
//
// Defensive: an enum registered after a lookup was not observed on a platform-only fixture
// (#4855 review); this pins the stamp's enum-registry term. In-process, because there the
// platform enums arrive with the symbols before any test body runs, so no AL test can put a
// lookup ahead of them. AllObjPopulateCostTests prove the key drives the memo end to end.
using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Mutates AlEnumMetadataRegistry, so it joins the registry's serial collection
// (EnumMetadataRegistryCollectionGuardTests enforces that). It never Clear()s: the ids below are
// unique to this file, and every other member of the collection clears for itself.
[Collection(EnumMetadataRegistrySerialCollection.Name)]
public sealed class AllObjInventoryStampTests
{
    // This file owns 884851xx.
    private const int NewEnumId = 88485101;
    private const int ReplacedEnumId = 88485102;
    private const int ExtendedEnumId = 88485103;

    private static object Stamp() =>
        typeof(RecordPatches).GetMethod("CurrentObjectInventoryStamp", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null)!;

    private static void Register(int id, string name) =>
        AlEnumMetadataRegistry.Register(id, name, new[] { "A", "B" }, new[] { 0, 1 });

    [Fact]
    public void AnEnumRegisteredMidBundle_MovesTheStamp()
    {
        var before = Stamp();
        Register(NewEnumId, "IT4851 Late Enum");
        Assert.NotEqual(before, Stamp());
    }

    /// <summary>A replaced entry leaves the registry's size unchanged; AllObj reads the name, so
    /// the stamp must still move. This is why the term is a mutation counter, not a count.</summary>
    [Fact]
    public void AnEnumReplacedUnderTheSameId_MovesTheStamp()
    {
        Register(ReplacedEnumId, "IT4851 First Name");
        var before = Stamp();
        Register(ReplacedEnumId, "IT4851 Second Name");
        Assert.NotEqual(before, Stamp());
    }

    /// <summary>An enumextension of an id no base enum declares is listed under that id too.</summary>
    [Fact]
    public void AnEnumExtensionRegisteredMidBundle_MovesTheStamp()
    {
        var before = Stamp();
        AlEnumMetadataRegistry.RegisterExtension(ExtendedEnumId, "IT4851 Late Extension", new[] { "C" }, new[] { 5 });
        Assert.NotEqual(before, Stamp());
    }
}
