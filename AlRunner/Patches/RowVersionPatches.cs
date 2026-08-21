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
// Two Cecil prepends (NclCecilRewrite), same pattern as BlobStoreIsolationPatches:
// TempTableDataProvider.Insert and .Modify each get the stamp before their body
// runs. The stamp writes the MutableRecordBuffer's own timestamp slot — the same
// `this[MetaTable.<SystemField>.FieldIndex] = value` idiom the buffer itself uses
// for SystemCreatedAt/SystemModifiedAt — so BOTH copies see it: Insert stores
// recordBuffer.ToArray() (stamp travels into the store) and the inserting record
// keeps its buffer (stamp answers the record's own HasBeenInserted immediately,
// mirroring SQL returning the new rowversion to the writer). Reads serve the stored
// buffer, so a record that Get()s the row afterwards carries the rowversion too.
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

public static class RowVersionPatches
{
    // Strictly increasing, process-wide, never 0 — rowversion semantics. Starts at 1
    // so the very first stamped row already answers HasBeenInserted = true.
    private static long _rowVersion;

    private static PropertyInfo? _pMetaTable;      // MutableRecordBuffer.MetaTable
    private static PropertyInfo? _pTimestampField; // NCLMetaTable.TimestampField (internal)
    private static PropertyInfo? _pFieldIndex;     // NCLMetaField.FieldIndex
    private static PropertyInfo? _pItem;           // MutableRecordBuffer.this[int]
    private static MethodInfo? _mCreate;           // NavBigInteger.Create(long)

    /// <summary>Cecil prepend on TempTableDataProvider.Insert — (this, companyToken, recordBuffer).</summary>
    public static void OnBeforeInsert(object? provider, int companyToken, object? recordBuffer)
        => Stamp(provider, recordBuffer);

    /// <summary>Cecil prepend on TempTableDataProvider.Modify — same first three arg slots.</summary>
    public static void OnBeforeModify(object? provider, int companyToken, object? recordBuffer)
        => Stamp(provider, recordBuffer);

    private static void Stamp(object? provider, object? recordBuffer)
    {
        if (recordBuffer == null || !BlobStoreIsolationPatches.IsDatabaseBacked(provider)) return;

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
        if (tsField == null) return;

        _pFieldIndex ??= tsField.GetType().GetProperty("FieldIndex",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"[RowVersionPatches] {tsField.GetType().Name}.FieldIndex property not found — " +
                "rowversion stamping cannot resolve its reflection target");
        var index = (int)_pFieldIndex.GetValue(tsField)!;

        _mCreate ??= typeof(Microsoft.Dynamics.Nav.Runtime.NavBigInteger).GetMethod(
            "Create", BindingFlags.Public | BindingFlags.Static, binder: null,
            new[] { typeof(long) }, modifiers: null)
            ?? throw new InvalidOperationException(
                "[RowVersionPatches] NavBigInteger.Create(long) method not found — " +
                "rowversion stamping cannot resolve its reflection target");
        _pItem ??= bufferType.GetProperty("Item",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"[RowVersionPatches] {bufferType.Name}.Item indexer not found — " +
                "rowversion stamping cannot resolve its reflection target");

        _pItem.SetValue(recordBuffer,
            _mCreate.Invoke(null, new object[] { System.Threading.Interlocked.Increment(ref _rowVersion) }),
            new object[] { index });
    }
}
