// AllObjInventoryStampTests — issue #4851 review. The AllObj row memo and each store's
// "already filled" set are keyed on RecordPatches.CurrentAllObjInventoryStamp(), so an input that
// grows WITHIN one bundle reaches AllObj only if the stamp moves when it grows.
//
// The live case is the enum registry: BcRuntime.EnsureSystemEnumsRegistered registers BC's
// platform enums lazily, on the first enum-field metadata read, and with Test Runner loaded that
// comes after Test Runner's first AllObj lookup. Only the stamp's enum-registry term sees it.
//
// Why in-process rather than an AL fixture: on a platform-only fixture the platform enums are
// already registered before any test body runs (measured: the first inventory walk already lists
// them), so an AL test cannot place a lookup before the registration. The two AllObj tests in
// AllObjPopulateCostTests prove the key drives the memo end to end; this pins the term.
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
        typeof(RecordPatches).GetMethod("CurrentAllObjInventoryStamp", BindingFlags.NonPublic | BindingFlags.Static)!
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
