// BlobStoreIsolationPatches — keeps a database-backed row's BLOB out of the
// record variable that inserted it, without disturbing the temporary-table shape.
//
// ── The divergence this exists for (issue #1751) ─────────────────────────────
//
// Both halves below are measured against a real service tier by corpus codeunit
// 60940 "Test Blob Uncomm Isolation", green on BC 27.5 and 28.3:
//
//   * Database-backed record — a BLOB written through CreateOutStream with NO
//     following Modify() is invisible to the stored row. A second Record instance
//     that Get()s the row reads it empty, and a re-Get() on the writing instance
//     discards the write.
//
//   * `temporary` record — the very same write IS visible through the store.
//     Get() reads the unpersisted bytes straight back, and so does a second
//     variable sharing the buffer via Copy(..., true).
//
// The corpus file was originally written asserting isolation for BOTH shapes;
// real BC rejected exactly the two temporary assertions and passed every control.
// So this is not a BC bug we may normalise away — it is two different contracts,
// and a blanket copy at the store boundary would fix one by breaking the other.
//
// ── Why the runner leaks the database case ───────────────────────────────────
//
// Every table in the runner is backed by Ncl's TempTableDataProvider (see
// RecordPatches.NavDataAccessSource_GetDataAccessForTable). That provider is the
// same code real BC runs for `temporary` records, so the runner inherits the
// temporary contract for database-backed tables too. Concretely, in Ncl:
//
//   TempTableDataProvider.Insert
//     items = recordBuffer.ToArray()                  // BLOB copied BY REFERENCE
//     new TempTableRecordBuffer(metaTable, items)
//     value.CloneBlobs(recordBuffer)                  // clones ONLY dirty BLOBs
//   DataAccess.InsertAsync
//     CreateNewBufferFromOutputBufferTransferBlobValuesFromOldRecord
//       newBuffer[i] = oldRecord.GetChangedFieldValue(i)   // SAME object again
//
// A BLOB that carried no value at Insert is not dirty, so CloneBlobs skips it and
// the stored row keeps the record's own NavBLOB — which the record then goes on
// using. `Content.CreateOutStream(o); o.WriteText(...)` mutates that one object
// and the stored row changes with it. On real BC this only ever happens for
// temporary records, because a database-backed row lives in SQL and there is no
// shared object to mutate.
//
// ── The fix ──────────────────────────────────────────────────────────────────
//
// Give the store its own NavBLOB at Insert, but ONLY for the providers that stand
// in for SQL. Two Cecil prepends (see NclCecilRewrite):
//
//   1. TempTableDataProvider.Insert  → OnBeforeStoreInsert(provider) records, for
//      the duration of this insert, whether the provider is database-backed.
//   2. TempTableRecordBuffer.CloneBlobs → DetachStoredBlobs(stored) deep-copies
//      every NavBLOB the stored row holds, so it shares none with the record.
//
// Prepends, not replacements: Ncl's own CloneBlobs body still runs afterwards and
// re-clones the dirty BLOBs exactly as before, so the write-before-Insert shape is
// untouched. For a temporary provider the flag is false, nothing is detached, and
// the aliasing real BC exhibits is preserved verbatim.
//
// Modify() needs no equivalent. TempTableDataProvider.Modify itself constructs no
// NavBLOB; the construction is in TempTableDataProvider.ModifyAllTrees, which Modify
// calls and is its only caller — and it stores
// `new NavBLOB(navBLOB.GetBytes(), useContentInstance: true)`, a distinct NavBLOB.
// So a second uncommitted write after Modify() does not reach the row. Verified by
// probe before this patch was written, and pinned by 60940's committed controls.
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Patches;

public static class BlobStoreIsolationPatches
{
    // Providers that stand in for SQL (i.e. were handed out for a NON-temporary
    // table). Weak so a provider is collectable with its DataAccess; the value is
    // an unused sentinel — membership is the whole signal.
    private static readonly ConditionalWeakTable<object, object> _databaseBackedProviders = new();
    private static readonly object _sentinel = new();

    // Set by the TempTableDataProvider.Insert prepend and read by the CloneBlobs
    // prepend, so it is never reset — which is safe only because CloneBlobs has
    // exactly ONE call site in the whole of Ncl: TempTableDataProvider.Insert, called
    // synchronously. Nothing else can observe a stale value, and every insert sets it
    // afresh. That is measured, not assumed — the call sites were counted by scanning
    // Microsoft.Dynamics.Nav.Ncl.dll (28.1) with Cecil, not read off a decompile.
    //
    // The whole patch's correctness rests on that count, so re-check it if a future BC
    // version changes shape: a second CloneBlobs caller outside Insert would read a
    // flag left over from an unrelated insert. Thread-static rather than static so
    // concurrent sessions cannot see each other's latch either.
    [ThreadStatic] private static bool _currentInsertIsDatabaseBacked;

    private static MethodInfo? _mNavBlobDeepCopy;

    /// <summary>
    /// Records that <paramref name="dataAccess"/> serves a non-temporary table, so
    /// rows inserted through it must not share BLOB objects with the record that
    /// inserted them. Called from RecordPatches.NavDataAccessSource_GetDataAccessForTable
    /// on every non-temporary hand-out (the same DataAccess may be handed out many
    /// times — registration is idempotent).
    /// </summary>
    public static void MarkDatabaseBacked(object? dataAccess)
    {
        if (dataAccess == null) return;
        var provider = dataAccess.GetType()
            .GetProperty("DataProvider", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(dataAccess);
        if (provider == null) return;
        // AddOrUpdate rather than Add: GetDataAccessForTable is called per Record
        // construction and returns the same cached DataAccess every time.
        _databaseBackedProviders.AddOrUpdate(provider, _sentinel);
    }

    /// <summary>
    /// Cecil prepend on TempTableDataProvider.Insert. Latches whether the row about
    /// to be stored belongs to a database-backed table.
    /// </summary>
    public static void OnBeforeStoreInsert(object? provider)
    {
        _currentInsertIsDatabaseBacked =
            provider != null && _databaseBackedProviders.TryGetValue(provider, out _);
    }

    /// <summary>
    /// Cecil prepend on TempTableRecordBuffer.CloneBlobs. For a database-backed
    /// table, replaces every NavBLOB in the freshly stored row with a deep copy, so
    /// the row shares no BLOB object with the record variable that inserted it.
    ///
    /// Observable equivalence: the copy holds exactly the bytes the record's BLOB
    /// held at Insert, so every read of the stored row answers as before. What
    /// changes is only what real BC also refuses to do — later in-memory writes on
    /// the inserting record no longer reach the row without Modify(). Corpus 60940
    /// pins both directions.
    ///
    /// Scanning values rather than metadata (`stored[i] is NavBLOB`) is deliberate:
    /// only BLOB fields ever hold a NavBLOB, and it avoids depending on the shape of
    /// NCLMetaTable.BlobFields.
    /// </summary>
    public static void DetachStoredBlobs(TempTableRecordBuffer? stored)
    {
        if (!_currentInsertIsDatabaseBacked || stored == null) return;

        for (var i = 0; i < stored.FieldCount; i++)
        {
            if (stored[i] is not NavBLOB blob) continue;

            _mNavBlobDeepCopy ??= blob.GetType().GetMethod("DeepCopy",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null, types: Type.EmptyTypes, modifiers: null)
                ?? throw new MissingMethodException(blob.GetType().FullName, "DeepCopy()");

            stored[i] = (NavValue)_mNavBlobDeepCopy.Invoke(blob, null)!;
        }
    }
}
