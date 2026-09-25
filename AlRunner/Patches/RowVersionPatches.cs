// RowVersionPatches — assign a rowversion ("timestamp", field 0) to every row written
// through a DATABASE-BACKED TempTableDataProvider, the way SQL Server does on every
// insert and update.
//
// ── The gap this closes (issue #1980) ────────────────────────────────────────
//
// NavRecord.HasBeenInserted for a non-temporary record is, verbatim from Ncl:
//
//     return !GetFieldValue(MetaTable.TimestampField).IsZeroOrEmpty;
//
// i.e. "does the row carry a rowversion" — which only SQL ever assigns. The runner's
// SQL stand-in (TempTableDataProvider, see RecordPatches.NavDataAccessSource_
// GetDataAccessForTable) never wrote the slot, so every stored row answered
// HasBeenInserted = false forever. NavForm.SaveRecordAsync branches on exactly that
// flag to pick Insert vs Modify, so CurrPage.SaveRecord() / CurrPage.Update(true)
// from a field's OnValidate issued an INSERT for a row the page had reached via
// GoToRecord — NavCSideDuplicateKeyException on the primary key. The rename path in
// SaveRecordAsync reads OldRecord.HasBeenInserted the same way, so a spot fix at the
// form would have repaired one consumer of a wrong answer instead of the answer.
//
// ── Why this is observably equivalent to real BC (loud-failures.md audit) ────
//
// SQL Server assigns a fresh, strictly-increasing rowversion to a row on every
// INSERT and UPDATE; AL can observe it only as "zero or not" (HasBeenInserted) and
// as an opaque monotonic BigInteger (Rec."timestamp"). A process-wide
// Interlocked.Increment counter starting above zero reproduces both observable
// properties. Temporary records are deliberately NOT stamped: on real BC a
// `temporary` record's timestamp stays zero (NCLMetaTable.SqlHasTimestamp is false
// for TableType.Temporary without a user-defined timestamp field), and its
// HasBeenInserted takes the ExistsAsync branch — the database-backed-only guard
// (BlobStoreIsolationPatches.IsDatabaseBacked) preserves that split.
//
// ── Mechanics ────────────────────────────────────────────────────────────────
//
// Cecil prepends (NclCecilRewrite), same pattern as BlobStoreIsolationPatches.
// Modify: the prepend on TempTableDataProvider.Modify writes the MutableRecordBuffer's
// own timestamp slot, which ModifyAllTrees copies into the stored row.
// Insert (#4642): the stamp lands only on an insert the provider ACCEPTS. The prepend
// on TempTableDataProvider.Insert resolves the slot and latches it; the prepend on
// TempTableRecordBuffer.CloneBlobs — reached only after primaryTree.Add succeeded —
// writes the stored row. The inserting record then receives it through Insert's
// output buffer (DataAccess.InsertAsync rebuilds the record from it on success), so a
// refused insert (InsertResult.RecordAlreadyExists, or a unique-index throw) leaves
// the record's timestamp at 0, as SQL does. Corpus 60247 pins both directions.
// Reads serve the stored buffer, so a record that Get()s the row afterwards carries
// the rowversion too.
// There is no timestamp-based optimistic-concurrency compare anywhere on the
// runner's modify path (checked: TempTableDataProvider.Modify compares nothing, and
// Ncl contains no record-changed check for this provider), so a record holding an
// older stamp than the store never trips anything.
//
// ── Loud-failures audit (issue #1986) ─────────────────────────────────────────
//
// The five reflection lookups below (MetaTable, TimestampField, FieldIndex, the
// buffer indexer, NavBigInteger.Create) are NOT allowed to fail quietly. Reverting
// to "no stamp" on a resolution failure is exactly the pre-#1980 bug this patch
// exists to close — HasBeenInserted going back to permanently false — so any lookup
// that cannot resolve throws InvalidOperationException naming the missing member,
// the same convention the rest of AlRunner/Patches uses for an internal invariant
// breaking (see e.g. NavRecordRefPatches, RecordPatches.AllObjVirtualTable).
// RunnerOutOfScopeException does not apply here: that type means "this AL surface
// is intentionally unsupported" (SMTP, HTTP egress, printing — see docs/scope.md),
// which a developer cannot fix by upgrading the runner. A BC build moving one of
// these members is a genuine runner defect with a fix available, not a permanent
// scope boundary, so a plain thrown exception carrying the member name is the
// right signal — it stops the run instead of quietly reintroducing #1980.
//
// The ONE legitimate quiet path stays quiet: `tsField == null` after the
// TimestampField property itself resolved and answered fine. That is a real BC
// answer — "this table has no timestamp field" — not a reflection failure, and
// must not be conflated with one (see the comment at that line).
using System.Reflection;

namespace AlRunner.Patches;

// NOTE: partial — the SystemId half (issue #2573: duplicate-SystemId Insert and
// SystemId-corrupting Modify) lives in RowVersionPatches.SystemIdIntegrity.cs. Both
// halves share the SAME two Cecil prepend hooks (OnBeforeInsert/OnBeforeModify are
// the ONLY prepend hooks NclCecilRewrite.cs wires onto TempTableDataProvider.Insert/
// Modify with the (this, companyToken, recordBuffer) argSlots:3 shape), so the
// SystemId logic is called from inside these same two methods rather than adding new
// prepend call sites. See that file for the SystemId-specific audit.
public static partial class RowVersionPatches
{
    // Strictly increasing, process-wide, never 0 — rowversion semantics. Starts at 1
    // so the very first stamped row already answers HasBeenInserted = true.
    private static long _rowVersion;

    /// <summary>
    /// Suppress the rowversion stamp for the duration of a `--test-data` hydration (#4123).
    ///
    /// A replay is not an AL Insert. The stamp exists so a row WRITTEN BY AL gets a fresh
    /// rowversion; a restored row already carries the backup's, and stamping over it is wrong
    /// twice: the value is lost, and — because seeding and stamping interleave per row — the
    /// backup's RELATIVE order between restored rows is destroyed. Measured: restoring
    /// 261652, 100, 500000 made AL see 261653, 261654, 500001, so the row with the LOWEST
    /// backup rowversion read as higher than the row before it.
    ///
    /// Same shape and same reason as SuppressSystemIdUniqueness (#2694): restores the ENCLOSING
    /// state on dispose, never unconditionally false, so a nested replay cannot re-arm the stamp
    /// for the outer one's remaining rows.
    /// </summary>
    public static IDisposable SuppressRowVersionStamp() => new RowVersionStampSuppressionScope();

    [ThreadStatic] private static bool _suppressRowVersionStamp;

    private sealed class RowVersionStampSuppressionScope : IDisposable
    {
        private readonly bool _previous;
        public RowVersionStampSuppressionScope()
        {
            _previous = _suppressRowVersionStamp;
            _suppressRowVersionStamp = true;
        }
        public void Dispose() => _suppressRowVersionStamp = _previous;
    }

    /// <summary>Whether the stamp is currently suppressed for a replay. Read by Stamp.</summary>
    internal static bool IsRowVersionStampSuppressed => _suppressRowVersionStamp;

    /// <summary>
    /// Raise the counter above the highest rowversion restored by `--test-data` (#4123).
    ///
    /// Called ONCE after a hydration completes, never per row — per-row seeding interleaved with
    /// per-row stamping is what destroyed the restored ordering (see SuppressRowVersionStamp).
    /// Real SQL has ONE monotonic sequence per database. Without this the next row a test writes
    /// takes rowversion 1 and sorts BEFORE every restored row, giving the runner two sequences
    /// interleaved wrongly. High-water mark, never lowering: tables hydrate in no defined order,
    /// so a later smaller maximum must not move the counter back.
    /// </summary>
    internal static void SeedFromRestoredRowVersion(long restored)
    {
        if (restored <= 0) return;
        long seen;
        while ((seen = System.Threading.Interlocked.Read(ref _rowVersion)) < restored)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _rowVersion, restored, seen) == seen)
                return;
        }
    }

    /// <summary>The value the next stamp would take. Tests only — the production path reads the
    /// counter exactly once, through Interlocked.Increment at the stamp site.</summary>
    internal static long PeekNextRowVersionForTests()
        => System.Threading.Interlocked.Read(ref _rowVersion) + 1;

    private static PropertyInfo? _pMetaTable;      // MutableRecordBuffer.MetaTable
    private static PropertyInfo? _pTimestampField; // NCLMetaTable.TimestampField (internal)
    private static PropertyInfo? _pFieldIndex;     // NCLMetaField.FieldIndex (shared with SystemIdField resolution)
    private static PropertyInfo? _pItem;           // MutableRecordBuffer.this[int] (shared with SystemId writes)
    private static MethodInfo? _mCreate;           // NavBigInteger.Create(long)

    /// <summary>Cecil prepend on TempTableDataProvider.Insert — (this, companyToken, recordBuffer).</summary>
    public static void OnBeforeInsert(object? provider, int companyToken, object? recordBuffer)
    {
        // #2573: refuse a second Insert carrying an already-used explicit SystemId
        // BEFORE BC's own Insert body runs. Must run before Stamp() — the rowversion
        // stamp is pointless work for an insert that is about to be refused.
        CheckNoDuplicateSystemId(provider, recordBuffer);
        // Always overwrite: a latch left by a refused insert must not stamp the next one.
        _pendingInsertStampIndex = ResolveStampIndex(provider, recordBuffer);
    }

    // Timestamp slot of the insert in flight on this thread, or null when it is not stamped.
    // Sound only because CloneBlobs has ONE call site, inside TempTableDataProvider.Insert
    // after primaryTree.Add succeeded (docs/blob-store-isolation.md#the-cloneblobs-call-count)
    // — re-check that count if a BC version changes shape.
    [ThreadStatic] private static int? _pendingInsertStampIndex;
    private static PropertyInfo? _pStoredItem; // TempTableRecordBuffer.this[int]

    /// <summary>Cecil prepend on TempTableRecordBuffer.CloneBlobs — (this = the stored row).
    /// Stamps the row an accepted Insert just stored (#4642).</summary>
    public static void OnInsertStored(object? storedRow)
    {
        var index = _pendingInsertStampIndex;
        _pendingInsertStampIndex = null;
        if (index is not int slot || storedRow == null) return;
        var rowType = storedRow.GetType();
        _pStoredItem ??= rowType.GetProperty("Item",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"[RowVersionPatches] {rowType.Name}.Item indexer not found — " +
                "rowversion stamping cannot resolve its reflection target");
        _pStoredItem.SetValue(storedRow, NextRowVersion(), new object[] { slot });
    }

    /// <summary>Cecil prepend on TempTableDataProvider.Modify — same first three arg slots.</summary>
    public static void OnBeforeModify(object? provider, int companyToken, object? recordBuffer)
    {
        // #2573: force the incoming buffer's SystemId back to the stored value BEFORE
        // BC's own Modify body runs its unconditional per-field copy (see
        // RowVersionPatches.SystemIdIntegrity.cs for why that copy includes SystemId).
        PreserveSystemIdOnModify(provider, recordBuffer);
        Stamp(provider, recordBuffer);
    }

    private static void Stamp(object? provider, object? recordBuffer)
    {
        if (ResolveStampIndex(provider, recordBuffer) is not int index) return;
        _pItem!.SetValue(recordBuffer, NextRowVersion(), new object[] { index });
    }

    private static object NextRowVersion()
    {
        _mCreate ??= typeof(Microsoft.Dynamics.Nav.Runtime.NavBigInteger).GetMethod(
            "Create", BindingFlags.Public | BindingFlags.Static, binder: null,
            new[] { typeof(long) }, modifiers: null)
            ?? throw new InvalidOperationException(
                "[RowVersionPatches] NavBigInteger.Create(long) method not found — " +
                "rowversion stamping cannot resolve its reflection target");
        return _mCreate.Invoke(null, new object[] { System.Threading.Interlocked.Increment(ref _rowVersion) })!;
    }

    /// <summary>The record buffer's timestamp slot when this write is to be stamped, else null.
    /// Resolves (and throws on a missing member) exactly as the stamp itself used to.</summary>
    private static int? ResolveStampIndex(object? provider, object? recordBuffer)
    {
        if (recordBuffer == null || !BlobStoreIsolationPatches.IsDatabaseBacked(provider)) return null;
        // #4123: a --test-data replay carries the backup's own rowversion; stamping over it
        // loses the value AND the restored rows' relative order. See SuppressRowVersionStamp.
        if (_suppressRowVersionStamp) return null;

        // No try/catch here — a failed lookup throws straight out of this method and
        // out of the Cecil-prepended TempTableDataProvider.Insert/Modify call it runs
        // ahead of, so the run stops with the failing member named instead of quietly
        // reverting to the pre-#1980 behaviour. See the file header for why this is
        // InvalidOperationException rather than RunnerOutOfScopeException.
        var bufferType = recordBuffer.GetType();
        _pMetaTable ??= bufferType.GetProperty("MetaTable",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"[RowVersionPatches] {bufferType.Name}.MetaTable property not found — " +
                "rowversion stamping cannot resolve its reflection target");
        var metaTable = _pMetaTable.GetValue(recordBuffer)
            ?? throw new InvalidOperationException(
                "[RowVersionPatches] record buffer has no MetaTable");

        _pTimestampField ??= metaTable.GetType().GetProperty("TimestampField",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"[RowVersionPatches] {metaTable.GetType().Name}.TimestampField property not found — " +
                "rowversion stamping cannot resolve its reflection target");
        var tsField = _pTimestampField.GetValue(metaTable);
        // A table without a timestamp field (companion-table shapes) simply has
        // nothing to stamp — same as SQL never returning a rowversion for it. This
        // is the ONE legitimate quiet return: the property above resolved and ran
        // fine, and truthfully answered "no timestamp field" — it is a real BC
        // answer, not a reflection failure, and must stay a quiet no-op.
        if (tsField == null) return null;

        _pFieldIndex ??= tsField.GetType().GetProperty("FieldIndex",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"[RowVersionPatches] {tsField.GetType().Name}.FieldIndex property not found — " +
                "rowversion stamping cannot resolve its reflection target");
        var index = (int)_pFieldIndex.GetValue(tsField)!;

        _pItem ??= bufferType.GetProperty("Item",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"[RowVersionPatches] {bufferType.Name}.Item indexer not found — " +
                "rowversion stamping cannot resolve its reflection target");
        return index;
    }
}
