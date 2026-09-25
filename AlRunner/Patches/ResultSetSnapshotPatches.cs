// ResultSetSnapshotPatches — a Find over a database-backed table reads the rows as they were
// when the find ran, not a live walk of the provider's AVL tree (#4678).
using System.Runtime.CompilerServices;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

/// <summary>
/// Every runner table is a <c>TempTableDataProvider</c>, whose <c>Find</c> hands back a LAZY
/// walk of a live AVL tree. A write to that tree while the walk is open (a second record
/// variable renaming the loop's sort key) re-shapes it under the enumerator, and <c>Next()</c>
/// then skips or stops early. SQL answers from the rows it read.
///
/// <para>Observably equivalent for a database-backed table: before any write reaches the
/// provider, every open walk over it is read to the end into a buffer, so each walk yields
/// exactly the rows, order and values it would have yielded had no write happened — what
/// corpus codeunit 60919 "FSK Tests" pins for a three-row set (#4678). Reading only when a
/// write arrives keeps a FindSet that is abandoned early O(rows read), as before.</para>
///
/// <para>`temporary` records are untouched: that is BC's own provider serving BC's own
/// temporary table, so whatever it does is what BC does.</para>
/// </summary>
public static class ResultSetSnapshotPatches
{
    private sealed class OpenWalks
    {
        public readonly List<WeakReference<SnapshotCursor>> Cursors = new();
    }

    private static readonly ConditionalWeakTable<object, OpenWalks> _openWalks = new();

    /// <summary>Wraps a non-FirstOnly Find result over <paramref name="provider"/> so a later
    /// write to the provider cannot change what the walk yields. Identity for a provider that
    /// does not stand in for SQL.</summary>
    internal static IEnumerable<ReadOnlyRecordBuffer> Wrap(object provider, IEnumerable<ReadOnlyRecordBuffer> rows)
        => BlobStoreIsolationPatches.IsDatabaseBacked(provider) ? new SnapshotOnWrite(provider, rows) : rows;

    /// <summary>
    /// Cecil prepend on every <c>TempTableDataProvider</c> write (Insert, Modify, Delete,
    /// ModifyAll, DeleteAll, Copy): drains every walk still open over this provider before the
    /// tree changes. Must run BEFORE the write — after it, the pre-write rows are gone.
    /// </summary>
    public static void OnBeforeProviderWrite(object? provider)
    {
        if (provider == null || !_openWalks.TryGetValue(provider, out var walks)) return;
        SnapshotCursor[] toDrain;
        lock (walks)
        {
            toDrain = walks.Cursors
                .Select(w => w.TryGetTarget(out var c) ? c : null)
                .Where(c => c != null)
                .ToArray()!;
            walks.Cursors.Clear();
        }
        foreach (var c in toDrain) c.Drain();
    }

    /// <summary>Number of walks currently registered on <paramref name="provider"/>; for tests.</summary>
    internal static int OpenWalkCount(object provider)
    {
        if (!_openWalks.TryGetValue(provider, out var walks)) return 0;
        lock (walks) return walks.Cursors.Count(w => w.TryGetTarget(out _));
    }

    private static void Register(object provider, SnapshotCursor cursor)
    {
        var walks = _openWalks.GetValue(provider, static _ => new OpenWalks());
        lock (walks)
        {
            walks.Cursors.RemoveAll(w => !w.TryGetTarget(out _));
            walks.Cursors.Add(new WeakReference<SnapshotCursor>(cursor));
        }
    }

    private static void Unregister(object provider, SnapshotCursor cursor)
    {
        if (!_openWalks.TryGetValue(provider, out var walks)) return;
        lock (walks)
            walks.Cursors.RemoveAll(w => !w.TryGetTarget(out var c) || ReferenceEquals(c, cursor));
    }

    private sealed class SnapshotOnWrite : IEnumerable<ReadOnlyRecordBuffer>
    {
        private readonly object _provider;
        private readonly IEnumerable<ReadOnlyRecordBuffer> _rows;

        public SnapshotOnWrite(object provider, IEnumerable<ReadOnlyRecordBuffer> rows)
        {
            _provider = provider;
            _rows = rows;
        }

        public IEnumerator<ReadOnlyRecordBuffer> GetEnumerator()
        {
            var cursor = new SnapshotCursor(_provider, _rows.GetEnumerator());
            Register(_provider, cursor);
            return cursor;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class SnapshotCursor : IEnumerator<ReadOnlyRecordBuffer>
    {
        private readonly object _provider;
        private IEnumerator<ReadOnlyRecordBuffer>? _live;
        private Queue<ReadOnlyRecordBuffer>? _drained;

        public SnapshotCursor(object provider, IEnumerator<ReadOnlyRecordBuffer> live)
        {
            _provider = provider;
            _live = live;
        }

        public ReadOnlyRecordBuffer Current { get; private set; } = null!;
        object System.Collections.IEnumerator.Current => Current;

        public bool MoveNext()
        {
            lock (this)
            {
                if (_drained != null)
                {
                    if (_drained.Count == 0) return false;
                    Current = _drained.Dequeue();
                    return true;
                }
                if (_live == null) return false;
                if (_live.MoveNext())
                {
                    Current = _live.Current;
                    return true;
                }
                CloseLive();
                return false;
            }
        }

        public void Drain()
        {
            lock (this)
            {
                if (_live == null) return;
                var q = new Queue<ReadOnlyRecordBuffer>();
                while (_live.MoveNext()) q.Enqueue(_live.Current);
                _drained = q;
                _live.Dispose();
                _live = null;
            }
        }

        private void CloseLive()
        {
            _live?.Dispose();
            _live = null;
            Unregister(_provider, this);
        }

        public void Reset() => throw new NotSupportedException();

        public void Dispose()
        {
            lock (this)
            {
                CloseLive();
                _drained = null;
            }
        }
    }
}
