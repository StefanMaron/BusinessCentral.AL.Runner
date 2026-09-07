// BlobStoreIsolationPatches — keeps a database-backed row's BLOB out of the record variable
// that inserted it, without disturbing the temporary-table shape.
//
// Two contracts, not one, and a blanket fix breaks the other half: on real BC a BLOB written
// with CreateOutStream and no following Modify() is invisible to a database-backed row, and
// visible through a `temporary` one. Corpus 60940 "Test Blob Uncomm Isolation" pins both,
// green on BC 27.5 and 28.3; corpus 60944 "Test Blob Rename Isolation" pins the Rename
// boundary, which is not symmetric with Insert/Modify. Issues #1751 and #1765.
//
// The runner leaks the database case because every table is backed by Ncl's
// TempTableDataProvider — the same code real BC runs for `temporary` records — so it inherits
// the temporary contract. The fix is three Cecil prepends: Insert latches whether the provider
// is database-backed, CloneBlobs deep-copies the stored row's BLOBs when it is, and
// ModifyAllTrees marks a renamed temporary row's carried-over BLOB as ineligible for
// CalcFields reload. Prepends, not replacements, so Ncl's own bodies still run.
//
// derivation, per-leg corpus results and the rejected value-keyed attempt:
// docs/blob-store-isolation.md
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

public static class BlobStoreIsolationPatches
{
    // Providers that stand in for SQL (i.e. were handed out for a NON-temporary
    // table). Weak so a provider is collectable with its DataAccess; the value is
    // an unused sentinel — membership is the whole signal.
    private static readonly ConditionalWeakTable<object, object> _databaseBackedProviders = new();
    private static readonly object _sentinel = new();

    // Never reset, which is safe only because CloneBlobs has exactly ONE call site in Ncl:
    // TempTableDataProvider.Insert, called synchronously (counted with Cecil over Ncl.dll 28.1
    // — docs/blob-store-isolation.md#the-cloneblobs-call-count). The whole patch rests on that
    // count: RE-CHECK IT if a future BC version changes shape, because a second CloneBlobs
    // caller outside Insert would read a flag left over from an unrelated insert. Thread-static
    // so concurrent sessions cannot see each other's latch either.
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
    /// Whether <paramref name="provider"/> was handed out for a NON-temporary table, i.e.
    /// stands in for SQL. Shared with RowVersionPatches, which must stamp rowversions on
    /// exactly the providers whose rows SQL would stamp — a `temporary` record's timestamp
    /// stays zero on real BC, and its HasBeenInserted takes the ExistsAsync branch instead.
    /// </summary>
    internal static bool IsDatabaseBacked(object? provider)
        => provider != null && _databaseBackedProviders.TryGetValue(provider, out _);

    /// <summary>
    /// Cecil prepend on TempTableRecordBuffer.CloneBlobs. For a database-backed table,
    /// replaces every NavBLOB in the freshly stored row with a deep copy, so the row shares no
    /// BLOB object with the record variable that inserted it.
    ///
    /// <para>Observably equivalent: the copy holds exactly the bytes the record's BLOB held at
    /// Insert, so every read of the stored row answers as before. What changes is only what
    /// real BC also refuses to do — later in-memory writes on the inserting record no longer
    /// reach the row without Modify(). Corpus 60940 pins both directions.</para>
    ///
    /// <para>Scanning values rather than metadata (`stored[i] is NavBLOB`) is deliberate: only
    /// BLOB fields ever hold a NavBLOB, and it avoids depending on NCLMetaTable.BlobFields.</para>
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

    // Rename boundary for `temporary` records (#1765). Corpus 60944 measures that a BLOB
    // committed with Modify() BEFORE a Rename() is LOST on real BC — CalcFields() after Get()
    // on the renamed row reads HasValue() = false — while the same sequence without the Rename
    // round-trips. Ncl's own store faithfully keeps those bytes, so the runner would otherwise
    // reload them; this table marks the (row, field index) pairs FlowFieldPatches.LoadBlobField
    // must treat as not-found, reproducing BC's measured result.
    //
    // Keyed by the ROW object (workTableBuffer), never by the NavBLOB value: Get()'s Find()-based
    // read materialises a different NavBLOB instance than TryGetValue returns, so a value-keyed
    // marker misses a path. The row object is stable — it is the same TempTableRecordBuffer Ncl
    // adds to the tree and returns back out.
    //
    // derivation, the per-leg 60944 results and the rejected value-keyed attempt:
    // docs/blob-store-isolation.md#rename
    private static readonly ConditionalWeakTable<object, HashSet<int>> _rowsWithUnloadableBlobFields = new();

    /// <summary>
    /// Cecil prepend on TempTableDataProvider.ModifyAllTrees. The trailing `primaryKeyChanged
    /// bool` is deliberately not forwarded: a rename is detected by `workTableBuffer` being a
    /// distinct object from `storedTableBuffer`, which Ncl's Modify() guarantees is equivalent
    /// (docs/blob-store-isolation.md#the-rename-fix).
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
        _mGetChangedFieldValue ??= BcShape.FindMethod(
            mrbType, "GetChangedFieldValue",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            "BLOB store isolation (per-record buffered BLOB writes)",
            "MutableRecordBuffer.GetChangedFieldValue",
            "a buffered BLOB write cannot be read back");
        if (_mGetChangedFieldValue == null) return;

        var metaTable = mrbType.GetProperty("MetaTable", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(mutableRecordBuffer);
        var getFieldByIndex = metaTable == null ? null : BcShape.FindMethod(
            metaTable.GetType(), "GetFieldByIndex",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            "BLOB store isolation (per-record buffered BLOB writes)",
            "NCLMetaTable.GetFieldByIndex",
            "the record's BLOB fields cannot be enumerated");
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

            // Mirror Ncl's OWN predicate exactly (`navBLOB != null && navBLOB.IsDirty`).
            // Non-null is NOT enough: a Rename's rekeyed buffer carries a non-null NavBLOB for
            // every field, dirty only where this call wrote bytes.
            var changedBlob = _mGetChangedFieldValue.Invoke(mutableRecordBuffer, new object[] { j }) as NavBLOB;
            if (changedBlob != null && changedBlob.IsDirty) continue;

            (ineligible ??= new HashSet<int>()).Add(j);
        }

        if (ineligible != null)
            _rowsWithUnloadableBlobFields.AddOrUpdate(workTableBuffer, ineligible);
    }

    private static MethodInfo? _mGetChangedFieldValue;

    /// <summary>
    /// Read by FlowFieldPatches.LoadBlobField before it reloads a temporary record's BLOB field
    /// by primary key on CalcFields(). A marked (row, field index) pair is treated as not-found,
    /// matching real BC losing that value after a Rename (corpus 60944).
    /// <paramref name="storedRow"/> is the TempTableRecordBuffer TryGetValue returned, NOT the
    /// BLOB value — value-object identity does not work here (see OnModifyAllTrees above).
    /// </summary>
    public static bool IsFieldIneligibleForCalcFieldsReload(object? storedRow, int fieldIdx)
        => storedRow != null
           && _rowsWithUnloadableBlobFields.TryGetValue(storedRow, out var fields)
           && fields.Contains(fieldIdx);
}
