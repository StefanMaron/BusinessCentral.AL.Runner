// RadEnumReplayTests — #2655. BcCompiler.ReplayRadEnumEntry puts a --watch RAD shadow-snapshot
// enum entry back into AlEnumMetadataRegistry. WatchEmitRegistriesReloadTests proves the replay
// end to end; this pins the one property it cannot see from AL: RegisterExtension appends, so a
// replay onto an enumextension a RAD delta already re-registered must not add a second entry
// (Format dedupes by ordinal, which hides the duplicate from every AL-visible read).
//
// Ids are private to this class and nothing is cleared, so it cannot disturb a parallel test
// using the same process-wide registry.
using Xunit;

namespace AlRunner.Tests;

public class RadEnumReplayTests
{
    private static int ExtensionEntries(int targetId, string name) =>
        AlEnumMetadataRegistry.SnapshotRaw(new[] { targetId })
            .Count(r => r.ExtendsTargetId == targetId && r.Entry.Name == name);

    [Fact]
    public void Replay_OntoAnAlreadyRegisteredExtension_LeavesOneEntry()
    {
        const int target = 918451;
        const string name = "RAD Replay Probe Ext A";
        AlEnumMetadataRegistry.RegisterExtension(target, name, new[] { "Restored" }, new[] { 5 }, null, new string?[] { "Restored Item" });
        var raw = AlEnumMetadataRegistry.SnapshotRaw(new[] { target }).Single(r => r.Entry.Name == name);

        BcCompiler.ReplayRadEnumEntry(raw.Entry, raw.ExtendsTargetId);

        Assert.Equal(1, ExtensionEntries(target, name));
    }

    [Fact]
    public void Replay_OntoAnEmptySlot_RegistersTheExtensionAgainstItsTarget()
    {
        const int target = 918452;
        const string name = "RAD Replay Probe Ext B";
        var entry = new AlEnumMetadataRegistry.Entry(target, name, new[] { "Restored" }, new[] { 5 },
            new[] { System.Array.Empty<int>() }, new string?[] { "Restored Item" });

        BcCompiler.ReplayRadEnumEntry(entry, target);

        Assert.Equal(1, ExtensionEntries(target, name));
        Assert.True(AlEnumMetadataRegistry.TryGet(target, out var merged));
        Assert.Equal(new string?[] { "Restored Item" }, merged.Captions);
        // Routed through RegisterExtension, never Register: the base slot stays empty (#2709).
        Assert.DoesNotContain(AlEnumMetadataRegistry.SnapshotRaw(new[] { target }), r => r.ExtendsTargetId == null);
    }
}
