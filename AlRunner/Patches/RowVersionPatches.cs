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
    private static bool _reflectionFailed;

    /// <summary>Cecil prepend on TempTableDataProvider.Insert — (this, companyToken, recordBuffer).</summary>
    public static void OnBeforeInsert(object? provider, int companyToken, object? recordBuffer)
        => Stamp(provider, recordBuffer);

    /// <summary>Cecil prepend on TempTableDataProvider.Modify — same first three arg slots.</summary>
    public static void OnBeforeModify(object? provider, int companyToken, object? recordBuffer)
        => Stamp(provider, recordBuffer);

    private static void Stamp(object? provider, object? recordBuffer)
    {
        if (recordBuffer == null || !BlobStoreIsolationPatches.IsDatabaseBacked(provider)) return;
        if (_reflectionFailed) return;

        try
        {
            var bufferType = recordBuffer.GetType();
            _pMetaTable ??= bufferType.GetProperty("MetaTable",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingMemberException(bufferType.Name, "MetaTable");
            var metaTable = _pMetaTable.GetValue(recordBuffer)
                ?? throw new InvalidOperationException("record buffer has no MetaTable");

            _pTimestampField ??= metaTable.GetType().GetProperty("TimestampField",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingMemberException(metaTable.GetType().Name, "TimestampField");
            var tsField = _pTimestampField.GetValue(metaTable);
            // A table without a timestamp field (companion-table shapes) simply has
            // nothing to stamp — same as SQL never returning a rowversion for it.
            if (tsField == null) return;

            _pFieldIndex ??= tsField.GetType().GetProperty("FieldIndex",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingMemberException(tsField.GetType().Name, "FieldIndex");
            var index = (int)_pFieldIndex.GetValue(tsField)!;

            _mCreate ??= typeof(Microsoft.Dynamics.Nav.Runtime.NavBigInteger).GetMethod(
                "Create", BindingFlags.Public | BindingFlags.Static, binder: null,
                new[] { typeof(long) }, modifiers: null)
                ?? throw new MissingMemberException("NavBigInteger", "Create(long)");
            _pItem ??= bufferType.GetProperty("Item",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingMemberException(bufferType.Name, "Item");

            _pItem.SetValue(recordBuffer,
                _mCreate.Invoke(null, new object[] { System.Threading.Interlocked.Increment(ref _rowVersion) }),
                new object[] { index });
        }
        catch (Exception ex)
        {
            // Loud once, then permanently off for this process — a half-working stamp
            // that throws on every write would take the whole store down, while a
            // missing stamp only reverts to the pre-#1980 behaviour it replaces.
            _reflectionFailed = true;
            Console.Out.WriteLine(
                $"[RowVersionPatches] rowversion stamping disabled — {ex.GetType().Name}: {ex.Message}");
        }
    }
}
