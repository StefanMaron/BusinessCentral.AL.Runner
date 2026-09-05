// StoredSystemIdIndexTests — the invalidation rules behind #2667's duplicate-SystemId index.
//
// The index replaces a walk of every stored row on every insert. Its only real risk is going
// stale, and the two directions are NOT equally bad:
//
//   * an index that LOST an entry misses a duplicate real BC would refuse — a wrong answer, but
//     the same wrong answer the runner gave before #2639 existed;
//   * an index holding a STALE entry refuses an insert real BC would ACCEPT — it fails a correct
//     test with a database error that has nothing to do with the AL under test.
//
// The second is the one to design against, and every test below is about a mutation the index
// does not observe: an insert that threw, a delete, a DeleteAll, a snapshot replay. In each case
// the store's row count no longer matches what the index last agreed with, and the index must
// rebuild rather than answer from memory.
//
// These drive the class directly with a fake store, so they pin the rule rather than a
// particular BC build's tree; the wiring into TempTableDataProvider.Insert is exercised
// end-to-end by the corpus's own SystemId contracts (codeunit 60061).

using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class StoredSystemIdIndexTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid C = Guid.Parse("33333333-3333-3333-3333-333333333333");

    /// <summary>A stand-in for the store, counting how often the O(rows) rebuild walk is taken.</summary>
    private sealed class FakeStore
    {
        internal readonly List<Guid> Rows = new();
        internal int Walks;
        internal IEnumerable<Guid> Enumerate() { Walks++; return Rows.ToList(); }
        internal int Count => Rows.Count;
    }

    [Fact]
    public void FirstSync_Rebuilds_AndAnswersFromTheStore()
    {
        var store = new FakeStore(); store.Rows.AddRange(new[] { A, B });
        var idx = new StoredSystemIdIndex();

        Assert.Equal(StoredSystemIdIndex.Unsynced, idx.SyncedRowCount);
        Assert.True(idx.SyncTo(store.Count, store.Enumerate), "an unsynced index must rebuild");

        Assert.Equal(1, store.Walks);
        Assert.True(idx.Contains(A));
        Assert.True(idx.Contains(B));
        Assert.False(idx.Contains(C));
        Assert.Equal(2, idx.SyncedRowCount);
    }

    [Fact]
    public void SecondSync_AtTheSameCount_DoesNotWalkTheStore()
    {
        // This is the whole point of the change: the common path must not be O(rows).
        var store = new FakeStore(); store.Rows.AddRange(new[] { A, B });
        var idx = new StoredSystemIdIndex();
        idx.SyncTo(store.Count, store.Enumerate);

        Assert.False(idx.SyncTo(store.Count, store.Enumerate), "a count that has not moved must not rebuild");
        Assert.Equal(1, store.Walks);
    }

    [Fact]
    public void NoteInserting_AnticipatesTheRowTheCallerIsAboutToLetThrough()
    {
        // The check runs AHEAD of the insert, so the index moves to one more than the store
        // holds right now. When the insert lands, the counts agree and the next check is O(1).
        var store = new FakeStore(); store.Rows.Add(A);
        var idx = new StoredSystemIdIndex();
        idx.SyncTo(store.Count, store.Enumerate);

        idx.NoteInserting(B, store.Count);
        store.Rows.Add(B);   // the insert lands

        Assert.Equal(store.Count, idx.SyncedRowCount);
        Assert.False(idx.SyncTo(store.Count, store.Enumerate), "a landed insert must not force a rebuild");
        Assert.Equal(1, store.Walks);
        Assert.True(idx.Contains(B));
    }

    [Fact]
    public void AnInsertThatThrew_IsCorrectedOnTheNextSync()
    {
        // The dangerous direction: the index noted a row that never landed. If it kept believing
        // that, it would refuse a later insert of the same id that real BC would accept.
        var store = new FakeStore(); store.Rows.Add(A);
        var idx = new StoredSystemIdIndex();
        idx.SyncTo(store.Count, store.Enumerate);

        idx.NoteInserting(B, store.Count);   // cleared to insert...
        // ...and the insert threw, so the store still holds one row.
        Assert.True(idx.Contains(B), "precondition: the phantom entry is present before the next sync");

        Assert.True(idx.SyncTo(store.Count, store.Enumerate), "a count one short must rebuild");
        Assert.False(idx.Contains(B), "a row that never landed must not survive as a phantom duplicate");
        Assert.True(idx.Contains(A));
    }

    [Fact]
    public void ADeletedRow_StopsCountingAsADuplicate()
    {
        var store = new FakeStore(); store.Rows.AddRange(new[] { A, B });
        var idx = new StoredSystemIdIndex();
        idx.SyncTo(store.Count, store.Enumerate);
        Assert.True(idx.Contains(A));

        store.Rows.Remove(A);   // a delete this index never saw

        Assert.True(idx.SyncTo(store.Count, store.Enumerate), "a dropped count must rebuild");
        Assert.False(idx.Contains(A), "re-inserting a deleted row's SystemId must be allowed");
        Assert.True(idx.Contains(B));
    }

    [Fact]
    public void DeleteAll_ClearsTheIndex()
    {
        var store = new FakeStore(); store.Rows.AddRange(new[] { A, B, C });
        var idx = new StoredSystemIdIndex();
        idx.SyncTo(store.Count, store.Enumerate);

        store.Rows.Clear();

        Assert.True(idx.SyncTo(store.Count, store.Enumerate));
        Assert.False(idx.Contains(A));
        Assert.False(idx.Contains(B));
        Assert.False(idx.Contains(C));
        Assert.Equal(0, idx.IndexedCount);
    }

    [Fact]
    public void Invalidate_ForcesARebuild_EvenWhenTheCountIsUnchanged()
    {
        // A snapshot replay (#2694) writes rows straight past the check under
        // SuppressSystemIdUniqueness, so the count can land back on its old value with entirely
        // different ids behind it. Nothing but an explicit invalidation catches that.
        var store = new FakeStore(); store.Rows.AddRange(new[] { A, B });
        var idx = new StoredSystemIdIndex();
        idx.SyncTo(store.Count, store.Enumerate);

        store.Rows.Clear();
        store.Rows.AddRange(new[] { B, C });   // same count, different rows
        Assert.False(idx.SyncTo(store.Count, store.Enumerate), "precondition: the count alone cannot see this");
        Assert.True(idx.Contains(A), "precondition: the index still believes the replaced row");

        idx.Invalidate();

        Assert.True(idx.SyncTo(store.Count, store.Enumerate), "an invalidated index must rebuild");
        Assert.False(idx.Contains(A));
        Assert.True(idx.Contains(C));
    }

    [Fact]
    public void RebuildIsLazy_TheCallbackIsNotInvokedWhenNothingDrifted()
    {
        // The callback is what costs O(rows). It must not be called on the common path — a
        // regression here would silently restore the quadratic this change exists to remove.
        var store = new FakeStore(); store.Rows.Add(A);
        var idx = new StoredSystemIdIndex();
        idx.SyncTo(store.Count, store.Enumerate);
        var walksAfterFirstSync = store.Walks;

        for (var i = 0; i < 100; i++) idx.SyncTo(store.Count, store.Enumerate);

        Assert.Equal(walksAfterFirstSync, store.Walks);
    }
}
