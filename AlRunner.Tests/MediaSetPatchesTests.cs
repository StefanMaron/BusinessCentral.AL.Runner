// MediaSetPatchesTests — contract tests for the store MediaSetPatches' NavMediaSet
// helpers share (see MediaSetPatches.cs file header for the full #1773 root-cause story).
//
// These pin the C# CONTRACT the fix depends on — not "what BC does" (that's the job of
// the companion corpus PR, StefanMaron/BusinessCentral.AL.Language.Tests#36, which proves
// the AL-observable behavior against real BC 27.5/28.3). What's provable here without a
// loaded BC runtime is the actual bug fix at the unit level: that membership recorded via
// one "self" instance is visible from a DIFFERENT "self" instance once both expose the
// same container Guid through Key.Value — which is the exact shape of the #1773 failure
// (a second AL record variable's NavMediaSet wrapper is never the same .NET object as the
// one that imported/inserted).
//
// FakeMediaValue below stands in for NavMediaValueBase: MediaSetPatches's helpers reach it
// purely by reflection (property "Key" returning something with a "Value" property, and a
// "SaveValueToTableField(Guid)" method), so a plain POCO with those members exercises the
// same reflection path the real Cecil-rewritten NavMediaSet/NavMediaValueBase would.
using System;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class MediaSetPatchesTests
{
    private sealed class FakeNavGuid
    {
        public Guid Value { get; }
        public FakeNavGuid(Guid value) => Value = value;
    }

    private sealed class FakeMediaValue
    {
        public FakeNavGuid Key { get; private set; } = new FakeNavGuid(Guid.Empty);
        public void SaveValueToTableField(Guid guid) => Key = new FakeNavGuid(guid);
    }

    [Fact]
    public void Count_NeverTouched_ReturnsZero()
    {
        var self = new FakeMediaValue();
        Assert.Equal(0, MediaSetPatches.NavMediaSet_get_ALCount(self));
    }

    [Fact]
    public void Remove_NeverTouched_ReturnsFalse_NoFieldWrite()
    {
        var self = new FakeMediaValue();
        Assert.False(MediaSetPatches.NavMediaSet_ALRemove(self, new object(), Guid.NewGuid()));
        // No membership was ever established, so nothing should have written the field.
        Assert.Equal(Guid.Empty, self.Key.Value);
    }

    [Fact]
    public void ALInsert_FirstCall_EstablishesContainerGuid_AndSavesItToTheField()
    {
        var self = new FakeMediaValue();
        var mediaId = Guid.NewGuid();

        Assert.True(MediaSetPatches.NavMediaSet_ALInsert(self, new object(), mediaId));

        // The container Guid must be persisted via the real (faked-here)
        // SaveValueToTableField — that's what makes it durable across Modify()+Get() in
        // the real runtime, since it becomes an ordinary field value.
        Assert.NotEqual(Guid.Empty, self.Key.Value);
        Assert.Equal(1, MediaSetPatches.NavMediaSet_get_ALCount(self));
        Assert.Equal(mediaId, MediaSetPatches.NavMediaSet_ALItem(self, 1));
    }

    [Fact]
    public void ALInsert_SecondCall_ReusesTheEstablishedContainerGuid()
    {
        var self = new FakeMediaValue();
        MediaSetPatches.NavMediaSet_ALInsert(self, new object(), Guid.NewGuid());
        var establishedSetId = self.Key.Value;

        MediaSetPatches.NavMediaSet_ALInsert(self, new object(), Guid.NewGuid());

        Assert.Equal(establishedSetId, self.Key.Value);
        Assert.Equal(2, MediaSetPatches.NavMediaSet_get_ALCount(self));
    }

    [Fact]
    public async System.Threading.Tasks.Task AddMediaToSetAsync_EmptySetId_GeneratesOne_AndRecordsMembership()
    {
        var mediaId = Guid.NewGuid();

        var effectiveSetId = await MediaSetPatches
            .NavMediaSet_AddMediaToSetAsync(new object(), new object(), Guid.Empty, mediaId);

        Assert.NotEqual(Guid.Empty, effectiveSetId);

        // Simulate the real caller's next step: BC's own ALImportAsync body persists
        // whatever AddMediaToSetAsync returned via SaveValueToTableField.
        var self = new FakeMediaValue();
        self.SaveValueToTableField(effectiveSetId);

        Assert.Equal(1, MediaSetPatches.NavMediaSet_get_ALCount(self));
        Assert.Equal(mediaId, MediaSetPatches.NavMediaSet_ALItem(self, 1));
    }

    [Fact]
    public async System.Threading.Tasks.Task AddMediaToSetAsync_ExistingSetId_ReturnsItUnchanged()
    {
        var setId = Guid.NewGuid();
        await MediaSetPatches.NavMediaSet_AddMediaToSetAsync(new object(), new object(), setId, Guid.NewGuid());

        var result = await MediaSetPatches
            .NavMediaSet_AddMediaToSetAsync(new object(), new object(), setId, Guid.NewGuid());

        Assert.Equal(setId, result);
    }

    // ── The actual #1773 regression, pinned at the unit level ────────────────────────
    //
    // Two DIFFERENT "self" instances (simulating two different NavMediaSet wrappers /
    // NavRecord materializations of the same row) must see the SAME membership once both
    // expose the same container Guid — this is what makes the fix survive Modify()+Get()
    // through a second AL record variable, not just the one that inserted.

    [Fact]
    public void Membership_IsVisible_FromADifferentSelfInstance_WithTheSameContainerGuid()
    {
        var writer = new FakeMediaValue();
        var mediaId = Guid.NewGuid();
        MediaSetPatches.NavMediaSet_ALInsert(writer, new object(), mediaId);

        // A second, distinct .NET object — never touched by the insert above — but
        // exposing the SAME container Guid, as it would after a real Get() re-reads the
        // row's own field bytes (which is what Key.Value round-trips through in the real
        // runtime).
        var reader = new FakeMediaValue();
        reader.SaveValueToTableField(writer.Key.Value);

        Assert.NotSame(writer, reader);
        Assert.Equal(1, MediaSetPatches.NavMediaSet_get_ALCount(reader));
        Assert.Equal(mediaId, MediaSetPatches.NavMediaSet_ALItem(reader, 1));
    }

    [Fact]
    public void Remove_ThroughADifferentSelfInstance_DecreasesCount_VisibleToBoth()
    {
        var writer = new FakeMediaValue();
        var mediaId = Guid.NewGuid();
        MediaSetPatches.NavMediaSet_ALInsert(writer, new object(), mediaId);

        var reader = new FakeMediaValue();
        reader.SaveValueToTableField(writer.Key.Value);

        Assert.True(MediaSetPatches.NavMediaSet_ALRemove(reader, new object(), mediaId));
        Assert.Equal(0, MediaSetPatches.NavMediaSet_get_ALCount(writer));
    }

    [Fact]
    public void ResetForTest_ClearsMembership_ForAKnownContainerGuid()
    {
        var self = new FakeMediaValue();
        MediaSetPatches.NavMediaSet_ALInsert(self, new object(), Guid.NewGuid());
        Assert.Equal(1, MediaSetPatches.NavMediaSet_get_ALCount(self));

        MediaSetPatches.ResetForTest();

        // Same container Guid, but the per-test store was cleared — matches BC's own
        // per-test transaction rollback (see MediaSetPatches.cs LIFETIME).
        Assert.Equal(0, MediaSetPatches.NavMediaSet_get_ALCount(self));
    }
}
