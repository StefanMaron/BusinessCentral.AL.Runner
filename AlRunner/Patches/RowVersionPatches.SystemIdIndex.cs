// StoredSystemIdIndex — the duplicate-SystemId check's index (issue #2667).
//
// #2639's guard answered "is this SystemId already stored?" by walking every stored row of the
// table on every insert. That is O(stored rows) per insert and O(rows^2) over a bulk load, and
// measured on a plain 3-field table it is not a rounding error: it was 93-97% of ALL the work a
// bulk insert did, and it grew quadratically (3.38x for a 2x row increase, 2.18x for a 1.5x
// one, against 4.00x/2.25x for a textbook quadratic and 2.00x/1.50x for a linear one).
//
// Real BC does not scan. It puts a clustered unique index on $systemId and lets SQL Server
// answer in constant time. The faithful analogue is an index, which is what this is.
//
// The whole difficulty is INVALIDATION, and the failure mode is asymmetric: an index that has
// LOST an entry misses a duplicate real BC would refuse, while an index holding a STALE entry
// refuses an insert real BC would accept. The second is worse -- it fails a correct test with
// a database error that has nothing to do with the AL under test -- so this class never trusts
// itself. It carries the store's row count as of its last sync and rebuilds whenever the store
// disagrees, which makes it self-correcting for every mutation it does not see:
//
//   * an insert that succeeds        -> count matches the anticipated one, no rebuild
//   * an insert that throws          -> count is one short, rebuild
//   * a delete, a DeleteAll          -> count dropped, rebuild
//   * a snapshot restore (rollback)  -> those inserts run under SuppressSystemIdUniqueness and
//                                       are not seen here at all, so the caller invalidates
//                                       explicitly AND the count check catches it
//
// A rebuild is one O(rows) walk, i.e. exactly what the old code did on EVERY insert, so the
// worst case is no worse than the behaviour this replaces.

namespace AlRunner.Patches;

/// <summary>
/// The set of SystemIds a single table store currently holds, kept in step with that store by
/// row count. Not thread-safe by itself: the caller holds the lock, because the decision to
/// rebuild, the lookup and the note-the-pending-insert have to be one atomic step.
/// </summary>
internal sealed class StoredSystemIdIndex
{
    /// <summary>Row count meaning "never synced, or known stale" — forces the next sync to rebuild.
    /// Negative so it can never equal a real count.</summary>
    internal const int Unsynced = -1;

    private readonly HashSet<Guid> _ids = new();
    private int _syncedRowCount = Unsynced;

    /// <summary>The store row count this index last agreed with. <see cref="Unsynced"/> until a sync.</summary>
    internal int SyncedRowCount => _syncedRowCount;

    /// <summary>How many ids are indexed — for tests; not a substitute for the row count, because
    /// two stored rows could in principle carry the same id (that is the bug being guarded against).</summary>
    internal int IndexedCount => _ids.Count;

    /// <summary>Force the next <see cref="SyncTo"/> to rebuild. Used when rows were written by a
    /// path this index does not observe.</summary>
    internal void Invalidate() => _syncedRowCount = Unsynced;

    /// <summary>
    /// Make the index agree with a store currently holding <paramref name="storedRowCount"/> rows,
    /// rebuilding from <paramref name="storedIds"/> only when the count says it drifted.
    /// <paramref name="storedIds"/> is a callback rather than a collection so the O(rows) walk is
    /// not paid on the common path where nothing drifted. Returns true when a rebuild happened.
    /// </summary>
    internal bool SyncTo(int storedRowCount, Func<IEnumerable<Guid>> storedIds)
    {
        if (_syncedRowCount == storedRowCount) return false;
        _ids.Clear();
        foreach (var id in storedIds()) _ids.Add(id);
        _syncedRowCount = storedRowCount;
        return true;
    }

    /// <summary>Is this id already stored? Only meaningful straight after a <see cref="SyncTo"/>.</summary>
    internal bool Contains(Guid id) => _ids.Contains(id);

    /// <summary>
    /// Record the row the caller is about to let through. The count moves to one MORE than the
    /// store holds right now, because this runs ahead of the insert it is clearing: if that
    /// insert lands the counts agree and the next check is O(1), and if it throws the store stays
    /// one short and the next check rebuilds.
    /// </summary>
    internal void NoteInserting(Guid id, int storedRowCountBeforeInsert)
    {
        _ids.Add(id);
        _syncedRowCount = storedRowCountBeforeInsert + 1;
    }
}
