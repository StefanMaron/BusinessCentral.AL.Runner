// ResultSetSnapshotPatchesTests — the runner-side contract behind #4678. What BC does is pinned
// upstream (corpus codeunit 60919 "FSK Tests"); these pin the mechanism that delivers it: a walk
// over a database-backed provider yields the rows it would have yielded with no write, once
// OnBeforeProviderWrite has run, and a `temporary` provider's walk stays BC's own live one.
using System.Collections.Generic;
using System.Linq;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class ResultSetSnapshotPatchesTests
{
    private sealed class FakeDataAccess
    {
        public FakeDataAccess(object provider) => DataProvider = provider;
        public object DataProvider { get; }
    }

    private static object DatabaseBackedProvider()
    {
        var provider = new object();
        BlobStoreIsolationPatches.MarkDatabaseBacked(new FakeDataAccess(provider));
        return provider;
    }

    // A live walk over a list, like the provider's walk over its AVL tree: it reads each
    // element only when asked, so a write made mid-walk changes what it yields next.
    private static IEnumerable<string> LiveWalk(List<string> store)
    {
        for (var i = 0; i < store.Count; i++) yield return store[i];
    }

    private static List<string> WalkWritingAfterFirst(object provider, List<string> store, bool announceWrite)
    {
        var seen = new List<string>();
        using var e = ResultSetSnapshotPatches.Wrap(provider, LiveWalk(store)).GetEnumerator();
        Assert.True(e.MoveNext());
        seen.Add(e.Current);
        if (announceWrite) ResultSetSnapshotPatches.OnBeforeProviderWrite(provider);
        // the write: the first row moves to the end, the way a renamed sort key moves it
        store.RemoveAt(0);
        store.Add("Z1");
        while (e.MoveNext()) seen.Add(e.Current);
        return seen;
    }

    [Fact]
    public void DatabaseBacked_WriteMidWalk_WalkYieldsTheRowsAsFound()
    {
        var provider = DatabaseBackedProvider();
        var seen = WalkWritingAfterFirst(provider, new List<string> { "A1", "A2", "A3" }, announceWrite: true);
        Assert.Equal(new[] { "A1", "A2", "A3" }, seen);
    }

    [Fact]
    public void Temporary_WriteMidWalk_WalkStaysLive()
    {
        var provider = new object(); // never marked: a `temporary` record's provider
        var seen = WalkWritingAfterFirst(provider, new List<string> { "A1", "A2", "A3" }, announceWrite: true);
        // the live walk resumes at index 1 of the rewritten list: A3, then the moved row
        Assert.Equal(new[] { "A1", "A3", "Z1" }, seen);
    }

    [Fact]
    public void DatabaseBacked_NoWrite_WalkIsLazyAndUnregistersAtTheEnd()
    {
        var provider = DatabaseBackedProvider();
        var reads = 0;
        IEnumerable<int> Counting() { for (var i = 0; i < 1000; i++) { reads++; yield return i; } }

        using (var e = ResultSetSnapshotPatches.Wrap(provider, Counting()).GetEnumerator())
        {
            Assert.True(e.MoveNext());
            Assert.Equal(1, reads);
            Assert.Equal(1, ResultSetSnapshotPatches.OpenWalkCount(provider));
        }
        Assert.Equal(0, ResultSetSnapshotPatches.OpenWalkCount(provider));

        Assert.Equal(1000, ResultSetSnapshotPatches.Wrap(provider, Counting()).Count());
        Assert.Equal(0, ResultSetSnapshotPatches.OpenWalkCount(provider));
    }

    [Fact]
    public void Write_OnAnotherProvider_DoesNotDrainThisWalk()
    {
        var provider = DatabaseBackedProvider();
        var other = DatabaseBackedProvider();
        var store = new List<string> { "A1", "A2", "A3" };
        var seen = new List<string>();
        using var e = ResultSetSnapshotPatches.Wrap(provider, LiveWalk(store)).GetEnumerator();
        Assert.True(e.MoveNext());
        seen.Add(e.Current);
        ResultSetSnapshotPatches.OnBeforeProviderWrite(other);
        Assert.Equal(1, ResultSetSnapshotPatches.OpenWalkCount(provider));
        store[1] = "B2";
        while (e.MoveNext()) seen.Add(e.Current);
        Assert.Equal(new[] { "A1", "B2", "A3" }, seen);
    }
}
