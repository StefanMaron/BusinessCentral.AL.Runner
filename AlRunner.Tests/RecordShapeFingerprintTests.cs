// RecordShapeFingerprintTests — issue #2335.
//
// ~/.cache/al-runner/bc-symbols is shared by every worktree of this repository, and its key used
// to carry only the .app's path, the .app's content hash, and a hand-maintained integer. Two
// branches that each add a field and each bump the same integer then read each other's entries.
//
// The failure mode is the dangerous one: not a deserialization error, but a payload that
// deserializes CLEANLY with the other branch's fields defaulted to null. A wrong answer replayed
// from cache. It cost one agent about an hour — a virtual table reading empty on a warm cache and
// full on a cold one, which looks exactly like a bug in the population code — and on 2026-09-05
// two separate branches reached for the same next CacheVersion within hours, both caught only by
// a rebase conflict on that one line.
//
// So the claim under test is specifically: TWO DIFFERENT SHAPES AT THE SAME VERSION MUST NOT
// PRODUCE THE SAME KEY. A test that only checked "the fingerprint is non-empty", or that the key
// contains the word "shape", would pass against an implementation that returned a constant.
using System.Reflection;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public class RecordShapeFingerprintTests
{
    // Two payload shapes that differ ONLY the way two concurrent branches differ: one has a field
    // the other does not. Under the old scheme both would key as v17 and silently share an entry.
    private sealed record BranchA(string ContentHash, List<int> Tables);
    private sealed record BranchB(string ContentHash, List<int> Tables, List<string>? PermissionSets);

    // Same members, different member TYPE — the other way a branch changes a payload.
    private sealed record BranchCTypeChanged(string ContentHash, List<string> Tables);

    // A nested record, to prove the walk is transitive rather than one level deep.
    private sealed record Leaf(int Id);
    private sealed record LeafPlus(int Id, string Caption);
    private sealed record NestingRoot(List<Leaf> Items);
    private sealed record NestingRootPlus(List<LeafPlus> Items);

    [Fact]
    public void AddingAField_ChangesTheFingerprint()
    {
        // The exact #2335 scenario. Nothing else in the key differs between the two branches.
        Assert.NotEqual(RecordShapeFingerprint.Of(typeof(BranchA)),
                        RecordShapeFingerprint.Of(typeof(BranchB)));
    }

    [Fact]
    public void ChangingAMembersType_ChangesTheFingerprint()
    {
        Assert.NotEqual(RecordShapeFingerprint.Of(typeof(BranchA)),
                        RecordShapeFingerprint.Of(typeof(BranchCTypeChanged)));
    }

    [Fact]
    public void AddingAFieldToANESTEDRecord_ChangesTheFingerprint()
    {
        // One level deep is not enough: BcAppSymbolCache's payload is a list of ParsedTable, and
        // most branches add a field to ParsedTable or ParsedField, not to the root.
        Assert.NotEqual(RecordShapeFingerprint.Of(typeof(NestingRoot)),
                        RecordShapeFingerprint.Of(typeof(NestingRootPlus)));
    }

    [Fact]
    public void TheSameShape_FingerprintsIdentically_AndRepeatably()
    {
        // The other direction, and the one that makes this usable: an unchanged shape must not
        // invalidate the cache. If this were unstable, every run would MISS and the fingerprint
        // would have replaced a correctness bug with a performance one.
        var first = RecordShapeFingerprint.Of(typeof(BranchA));
        var second = RecordShapeFingerprint.Of(typeof(BranchA));
        Assert.Equal(first, second);
        Assert.Equal(16, first.Length);
    }

    [Fact]
    public void TheDescriptionNamesTheMemberThatChanged()
    {
        // A bare hash cannot tell anyone WHAT moved. The description is what makes a surprise
        // cache miss diagnosable instead of mysterious.
        var a = RecordShapeFingerprint.Describe(typeof(BranchA));
        var b = RecordShapeFingerprint.Describe(typeof(BranchB));

        Assert.DoesNotContain("PermissionSets", a);
        Assert.Contains("PermissionSets", b);
    }

    [Fact]
    public void TheWalkDoesNotRecurseIntoBclTypes()
    {
        // Walking into BCL internals would make the fingerprint depend on the .NET version, so
        // an SDK bump would invalidate every cache entry for no reason. `string` and `int` are
        // recorded by NAME (they must be, or a type change would be invisible) but their members
        // are not walked.
        var description = RecordShapeFingerprint.Describe(typeof(BranchA));

        Assert.Contains("System.String", description);
        // String's own members must not appear — if they did, the description would carry the
        // BCL's shape rather than ours.
        Assert.DoesNotContain("Chars:", description);
        Assert.DoesNotContain("Length:System.Int32", description);
    }

    // #4505: a record reachable ONLY as a Dictionary VALUE was never walked into, so gaining a
    // member left the fingerprint unchanged and the cache served a stale payload at the identical
    // key. Silent and warm-only: CI provisions a fresh cache per leg, so the legs stay green
    // forever while a developer's box reads back the old shape.
    //
    // These fixtures hold the type NAME constant and vary only the member list, which is the
    // shape that actually occurs -- two branches where one adds a field. The obvious probe
    // (Dictionary<int, Leaf> against Dictionary<int, LeafPlus>) PASSES against the defect,
    // because the differing type names reach the description even when the walk does not.
    private sealed record DictLeaf(int Id);
    private sealed record DictRoot(Dictionary<int, DictLeaf> Items);

    [Fact]
    public void AddingAMemberToADictionaryVALUE_ChangesTheFingerprint()
    {
        // The value type is reachable only through the dictionary, so this is the whole claim:
        // the walk must descend through BOTH generic arguments, not just single-argument
        // containers like List<>.
        var before = RecordShapeFingerprint.Describe(typeof(DictRoot));

        Assert.Contains("DictLeaf", before);
        // Asserted positively, not merely "the fingerprints differ": the defect is that the
        // leaf's MEMBERS never reach the description, and a name-only mention is what made it
        // look correct.
        Assert.Contains("Id:System.Int32", before);
    }

    [Fact]
    public void ADictionaryKEY_IsAlsoWalked()
    {
        // The key side needs its own arm: unwrapping only GetGenericArguments()[0] would pass
        // the value-side test above while leaving the key unwalked, and a key record changing
        // shape is the same silent-stale-entry defect.
        var description = RecordShapeFingerprint.Describe(typeof(KeyedRoot));

        Assert.Contains("KeyLeaf", description);
        Assert.Contains("Code:System.String", description);
    }

    private sealed record KeyLeaf(string Code);
    private sealed record KeyedRoot(Dictionary<KeyLeaf, int> Items);

    [Fact]
    public void ASelfReferencingRecord_Terminates()
    {
        // Records referencing each other is normal; the walk must not spin. No assertion beyond
        // "it returns" is possible here, so the name says that is the whole claim.
        var fingerprint = RecordShapeFingerprint.Of(typeof(SelfReferencing));
        Assert.False(string.IsNullOrEmpty(fingerprint));
    }

    private sealed record SelfReferencing(int Id, SelfReferencing? Next);

    [Fact]
    public void BcAppSymbolCacheKey_CarriesTheShape_AndTheVersion()
    {
        // Wired, not merely available. The cache path must move when the shape moves, which is
        // the property that actually protects a concurrent branch.
        var shape = ShapeOfTheRealPayload();
        Assert.False(string.IsNullOrEmpty(shape));

        var path = AlRunner.Patches.BcAppSymbolCache.CachePathForVersionForTests(
            "/tmp/example.app", "deadbeef", AlRunner.Patches.BcAppSymbolCache.CacheVersionForTests);
        Assert.False(string.IsNullOrEmpty(path));

        // The claim that matters, asserted directly rather than inferred: the KEY carries the
        // shape. The path is a hash, so this is invisible from the path alone — which is why
        // BuildKeyForTests exists.
        var key = KeyOfTheRealCache();
        Assert.Contains($"shape:{shape}", key, StringComparison.Ordinal);
        Assert.Contains($"v{AlRunner.Patches.BcAppSymbolCache.CacheVersionForTests}", key, StringComparison.Ordinal);

        // And the version still discriminates, so the fingerprint ADDED a dimension rather than
        // replacing one — both belong in the key, because a fingerprint cannot see a parse change
        // that leaves the shape alone.
        var otherVersion = AlRunner.Patches.BcAppSymbolCache.CachePathForVersionForTests(
            "/tmp/example.app", "deadbeef", AlRunner.Patches.BcAppSymbolCache.CacheVersionForTests + 1);
        Assert.NotEqual(path, otherVersion);
    }

    private static string KeyOfTheRealCache()
    {
        var m = typeof(AlRunner.Patches.BcAppSymbolCache)
            .GetMethod("BuildKeyForTests", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("BuildKeyForTests is gone — the key may no longer be built in one place");
        return (string)m.Invoke(null, new object[]
        {
            "/tmp/example.app", "deadbeef", AlRunner.Patches.BcAppSymbolCache.CacheVersionForTests
        })!;
    }

    private static string ShapeOfTheRealPayload()
    {
        var prop = typeof(AlRunner.Patches.BcAppSymbolCache)
            .GetProperty("PayloadShapeForTests", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("PayloadShapeForTests is gone — the key may no longer carry the shape");
        return (string)prop.GetValue(null)!;
    }

    [Fact]
    public void TheRealPayloadShape_ReachesParsedFieldsMembers()
    {
        // The regression this exists to prevent is a branch adding a field to ParsedTable or
        // ParsedField and the cache not noticing. Both are several levels below CachePayload, so
        // this asserts the walk actually gets there rather than stopping at the root's own list.
        var description = RecordShapeFingerprint.Describe(
            typeof(AlRunner.Patches.BcAppSymbolCache).Assembly.GetType("AlRunner.Patches.ParsedTable")
            ?? throw new InvalidOperationException("ParsedTable not found"));

        Assert.Contains("ParsedField", description);
        // A member that only exists on ParsedField, so reaching it proves the walk descended
        // through the list rather than merely naming the element type.
        Assert.Contains("FieldId", description);
    }
    // #4516: a runner-owned type DERIVING from a BCL type put the base's inherited public members
    // (List<T>.Capacity/Count/Item) into the description, so the key moved with the .NET SDK.
    // The type NAME is held constant across both assertions below, so a pass cannot come from a
    // differing name reaching the description (#4505's trap).
    private sealed class OwnedList : List<Leaf> { }
    private sealed record OwnedListRoot(OwnedList Items);

    [Fact]
    public void ABclBasesInheritedMembers_AreNotInTheDescription()
    {
        var description = RecordShapeFingerprint.Describe(typeof(OwnedListRoot));

        Assert.DoesNotContain("Capacity:", description);
        Assert.DoesNotContain("Count:", description);
        Assert.DoesNotContain("Item:", description);
        // Stated positively too: the owned type is recorded with its base's name, no members of its own.
        Assert.Contains("+OwnedList:System.Collections.Generic.List`1<AlRunner.Tests.RecordShapeFingerprintTests+Leaf>{}", description);
    }

    // A non-collection BCL base leaks the same way (Exception.Message, .HResult, ...).
    private sealed class OwnedError : Exception { public int Code { get; init; } }

    [Fact]
    public void ANonCollectionBclBase_LeaksNothingEither_ButTheOwnMemberStays()
    {
        var description = RecordShapeFingerprint.Describe(typeof(OwnedError));

        Assert.DoesNotContain("Message:", description);
        Assert.DoesNotContain("HResult:", description);
        Assert.Contains("Code:System.Int32", description);
    }

    // The other side of the boundary: a member inherited from a RUNNER-OWNED base is ours, and
    // must stay in the key. BindingFlags.DeclaredOnly would drop it, so a branch adding a field
    // to a shared base record would stop changing the fingerprint.
    private abstract record OwnBase(int BaseId);
    private sealed record OwnDerived(string Name) : OwnBase(1);

    [Fact]
    public void AMemberInheritedFromAnOwnBase_IsStillInTheDescription()
    {
        var description = RecordShapeFingerprint.Describe(typeof(OwnDerived));

        Assert.Contains("BaseId:System.Int32", description);
        Assert.Contains("Name:System.String", description);
    }

    // Review of PR #4586: filtering on the declaring type must not cut the only path from an owned
    // collection subclass to its element type. The three variants differ only inside their
    // enclosing class, so the descriptions are compared with that prefix normalised away -- a
    // pass cannot come from a differing type name.
    private static class MapV1
    {
        public sealed record ProbeLeaf(int ProbeLeafId);
        public sealed class OwnedMap : Dictionary<string, ProbeLeaf> { }
        public sealed record MapRoot(OwnedMap Entries);
    }

    private static class MapV2
    {
        public sealed record ProbeLeaf(int ProbeLeafId, string? Added);
        public sealed class OwnedMap : Dictionary<string, ProbeLeaf> { }
        public sealed record MapRoot(OwnedMap Entries);
    }

    private static class MapV3
    {
        public sealed record ProbeLeaf(int ProbeLeafId);
        public sealed class OwnedMap : Dictionary<int, ProbeLeaf> { }
        public sealed record MapRoot(OwnedMap Entries);
    }

    private static string Normalised(Type root) =>
        RecordShapeFingerprint.Describe(root).Replace(root.DeclaringType!.Name + "+", "V+");

    [Fact]
    public void AnOwnedCollectionSubclass_KeepsItsElementTypeInTheDescription()
    {
        var description = RecordShapeFingerprint.Describe(typeof(MapV1.MapRoot));

        Assert.Contains("ProbeLeafId:System.Int32", description);
        Assert.DoesNotContain("Count:", description);
        Assert.DoesNotContain("Comparer:", description);
    }

    [Fact]
    public void AnOwnedCollectionSubclass_LeafMemberChange_ChangesTheDescription()
    {
        Assert.NotEqual(Normalised(typeof(MapV1.MapRoot)), Normalised(typeof(MapV2.MapRoot)));
    }

    [Fact]
    public void AnOwnedCollectionSubclass_BaseTypeArgumentChange_ChangesTheDescription()
    {
        Assert.NotEqual(Normalised(typeof(MapV1.MapRoot)), Normalised(typeof(MapV3.MapRoot)));
    }
}
