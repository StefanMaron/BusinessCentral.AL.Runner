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
using System.Collections.Generic;
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

    // ── Rename store-aliasing boundary for `temporary` records (issue #1765) ────
    //
    // Follow-up measurement to #1751/60940: does Rename() have the same BLOB
    // store-aliasing boundary as Insert()/Modify()? Corpus 60944 "Test Blob Rename
    // Isolation" (green on BC 27.5 and 28.3) answers NO, and the shape is not
    // symmetric with Insert/Modify at all:
    //
    //   * Database-backed record — Rename() re-persists the record variable's whole
    //     current buffer under the new key (proven by the scalar-field control:
    //     an uncommitted plain-Text write also survives a Rename). A BLOB committed
    //     earlier with Modify() survives an unrelated Rename() intact — expected,
    //     needs no patch, and the runner already matches it via Ncl's own
    //     TempTableDataProvider.Modify (Rename routes through the very same method
    //     with primaryKeyChanged=true — see ModifyAllTrees below).
    //
    //   * `temporary` record — an uncommitted write (never Modify()'d) still leaks
    //     through a Rename, unsurprising and already covered by the temporary half
    //     of #1751's aliasing. But a BLOB that WAS committed with Modify() BEFORE
    //     the Rename() call is LOST — CalcFields() after Get() on the renamed row
    //     reads HasValue() = false, even though the exact same Insert→write→Modify
    //     sequence WITHOUT the Rename() round-trips correctly (60940's temporary
    //     positive control). Measured, not assumed: this is the one genuine surprise
    //     of 60944, identical on both BC versions.
    //
    // ── Why the runner does not reproduce the loss on its own ───────────────────
    //
    // TempTableDataProvider.Modify is also what Rename() calls (RecordImplementation
    // .RenameRecordAsync builds a rekeyed buffer and calls dataAccess.ModifyAsync,
    // same as a plain Modify). Its private ModifyAllTrees only replaces a BLOB field
    // in the row being stored when Ncl's own dirty-tracking calls that field
    // "changed" (`GetChangedFieldValue(j) != null && navBLOB.IsDirty`) — for a
    // Rename that does not touch the BLOB, it is not dirty, so the row keeps
    // whatever NavBLOB object the pre-rename baseline already held. For a
    // `temporary` table that object legitimately still carries the bytes Modify()
    // persisted — Ncl's own store faithfully keeps them. Our own
    // FlowFieldPatches.LoadBlobField (added for #1724) then finds that row by
    // primary key on the next CalcFields() and loads it — correctly, by our own
    // read of Ncl's state, but NOT what real BC's temporary-table blob JIT-load
    // does once a Rename has run. Real BC's mechanism is closed; the measured
    // *result* is what corpus 60944 pins, and this patch reproduces the result.
    //
    // ── The fix ──────────────────────────────────────────────────────────────────
    //
    // A third Cecil prepend, on the SAME TempTableDataProvider.ModifyAllTrees that
    // #1751's comment above already names as Modify's real BLOB-write path. When,
    // for a NON-database-backed (temporary) provider, this call is a rename
    // (`workTableBuffer` is a fresh buffer, not the same object as the removed
    // `storedTableBuffer`) AND a BLOB field is NOT dirty on this call (it is
    // carrying over a value from before the rename, not a fresh write), that FIELD
    // INDEX on the *row* (`workTableBuffer`, the object Ncl adds to the AVL tree) is
    // marked as ineligible for FlowFieldPatches.LoadBlobField's by-primary-key
    // reload fallback.
    //
    // Keyed by the row object, not the NavBLOB value object: a first attempt marked
    // the NavBLOB instance itself, but Get()'s own Find()-based read materialises a
    // DIFFERENT NavBLOB instance for `parentBuffer.ReadOnlyBuffer` than the one
    // TempTableDataProvider.TryGetValue returns from the tree directly (same bytes,
    // different object identity) — so a value-keyed marker silently failed to catch
    // the one path (LoadBlobField's Step 1, sizing the JIT-load placeholder from
    // `original.ALLength`) that made ALHasValue true regardless of whether Step 4's
    // byte-copy ran. The *row* object, in contrast, is verified stable: it is
    // literally the same `TempTableRecordBuffer` `list[k].Add(workTableBuffer)`
    // inserts into the tree and `TryGetValue` returns back out.
    //
    // A future successful Modify() calls this same method again with a fresh
    // `workTableBuffer`/`storedTableBuffer` pair (Ncl always constructs a new
    // TempTableRecordBuffer per Modify — see TempTableDataProvider.Modify above),
    // so the OLD row object holding the marker is simply never looked up again;
    // ConditionalWeakTable lets it be collected once nothing else references it.
    //
    // Scoped to non-database-backed providers only: the database-backed shape
    // (test3/Blob_CommittedWrite_Rename_SecondInstanceGet_ReadsWrittenBytes) must
    // keep working — and does, because MarkDatabaseBacked() (see above) means
    // _databaseBackedProviders.TryGetValue succeeds for it and this method never
    // marks anything for that provider.
    private static readonly ConditionalWeakTable<object, HashSet<int>> _rowsWithUnloadableBlobFields = new();

    /// <summary>
    /// Cecil prepend on TempTableDataProvider.ModifyAllTrees (`this`, mutableRecordBuffer,
    /// workTableBuffer, storedTableBuffer — the trailing `primaryKeyChanged bool` is not
    /// forwarded; renames are detected via `workTableBuffer` being a distinct object from
    /// `storedTableBuffer`, which Ncl's own Modify() guarantees is true if and only if
    /// primaryKeyChanged was true — see TempTableDataProvider.Modify's two branches).
    /// </summary>
    public static void OnModifyAllTrees(
        object? provider, object? mutableRecordBuffer, object? workTableBuffer, object? storedTableBuffer)
    {
        if (mutableRecordBuffer == null || workTableBuffer == null) return;
        // Database-backed: never mark. Ncl's own dirty-tracked BLOB write already
        // does the right thing there, and 60944's db-backed committed control
        // (which must keep passing) goes through this exact same method.
        if (provider != null && _databaseBackedProviders.TryGetValue(provider, out _)) return;
        // Not a rename (plain Modify): workTableBuffer IS storedTableBuffer, nothing
        // to mark — a same-buffer Modify already uses Ncl's normal dirty-BLOB path.
        if (ReferenceEquals(workTableBuffer, storedTableBuffer)) return;

        var mrbType = mutableRecordBuffer.GetType();
        _mGetChangedFieldValue ??= mrbType.GetMethod("GetChangedFieldValue",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (_mGetChangedFieldValue == null) return;

        var metaTable = mrbType.GetProperty("MetaTable", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(mutableRecordBuffer);
        var getFieldByIndex = metaTable?.GetType().GetMethod("GetFieldByIndex", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var fieldCount = mrbType.GetProperty("FieldCount", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(mutableRecordBuffer) is int fc ? fc : 0;
        if (metaTable == null || getFieldByIndex == null) return;

        HashSet<int>? ineligible = null;
        for (var j = 0; j < fieldCount; j++)
        {
            var field = getFieldByIndex.Invoke(metaTable, new object[] { j });
            var fieldNclType = field?.GetType()
                .GetProperty("FieldNclType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(field);
            if (fieldNclType is not NavNclType.NavBlob) continue;

            // Mirror Ncl's OWN predicate for "this call writes the BLOB" exactly
            // (TempTableDataProvider.ModifyAllTrees: `navBLOB != null && navBLOB.IsDirty`).
            // GetChangedFieldValue(j) being non-null is NOT enough on its own — a
            // Rename's rekeyed buffer carries a non-null NavBLOB for every field
            // (built via `recordBuffer.ToArray()`), but that object's OWN IsDirty flag
            // is false unless this call is the one that actually wrote new bytes.
            var changedBlob = _mGetChangedFieldValue.Invoke(mutableRecordBuffer, new object[] { j }) as NavBLOB;
            if (changedBlob != null && changedBlob.IsDirty) continue;

            (ineligible ??= new HashSet<int>()).Add(j);
        }

        if (ineligible != null)
            _rowsWithUnloadableBlobFields.AddOrUpdate(workTableBuffer, ineligible);
    }

    private static MethodInfo? _mGetChangedFieldValue;

    /// <summary>
    /// Read by FlowFieldPatches.LoadBlobField before it reloads a temporary record's
    /// BLOB field by primary key on CalcFields(). A marked (row, field index) pair
    /// must be treated as not-found — matching real BC losing that value after a
    /// Rename (60944) — rather than faithfully reloading what Ncl's own store still
    /// (correctly, by its own rules) holds. <paramref name="storedRow"/> is the
    /// TempTableRecordBuffer TryGetValue returned, NOT the BLOB value itself — see
    /// the comment above OnModifyAllTrees for why value-object identity does not
    /// work for this check.
    /// </summary>
    public static bool IsFieldIneligibleForCalcFieldsReload(object? storedRow, int fieldIdx)
        => storedRow != null
           && _rowsWithUnloadableBlobFields.TryGetValue(storedRow, out var fields)
           && fields.Contains(fieldIdx);
}
