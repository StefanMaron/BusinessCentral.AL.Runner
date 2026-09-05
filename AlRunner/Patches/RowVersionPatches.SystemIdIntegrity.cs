// RowVersionPatches.SystemIdIntegrity — SystemId is a physically unique, immutable
// key on a database-backed table, even though it is not one of the table's declared
// AL keys. Two defects (issue #2573), both invisible until AL reads a row back by
// SystemId:
//
// ── 1. Insert accepts a duplicate SystemId ───────────────────────────────────────
//
// Real SQL Server enforces uniqueness on the $systemId column with a clustered
// primary key, so inserting two rows with the same explicit SystemId is a
// unique-constraint violation. NCL's TempTableDataProvider — the runner's stand-in
// for SQL, see RecordPatches.NavDataAccessSource_GetDataAccessForTable — maintains
// its own generic unique-index dictionary for exactly this kind of check, built in
// TempTableDataProvider.EnsureTreeCreated from `table.Keys.Where(k => k.Unique &&
// !k.IsSystemIdKey)` (decompiled, BC 28.1 Ncl.dll). SystemId is deliberately
// EXCLUDED from that dictionary, because an ordinary `temporary` record may
// legitimately repeat an empty SystemId — real BC never enforces uniqueness on a
// temp table's SystemId either, since it is a plain in-memory field there, not a
// SQL column. The runner reuses the SAME provider for BOTH shapes, so accepting a
// duplicate explicit SystemId on a database-backed table was silently wrong.
//
// ── 2. Modify can silently change an existing row's SystemId ────────────────────
//
// TempTableDataProvider.ModifyAllTrees copies every non-BLOB field from the
// incoming buffer into the stored row unconditionally:
//
//     for (int j = 0; j < workTableBuffer.FieldCount; j++)
//         ... workTableBuffer[j] = mutableRecordBuffer[j];
//
// (decompiled, BC 28.1 Ncl.dll) — field 0 (SystemId) is not special-cased, unlike
// the Insert-side uniqueIndexes exclusion above. On real SQL Server this is moot:
// an UPDATE statement never includes the $systemId column at all, so nothing
// AL-observable can ever change it via Modify(). The runner's in-memory analogue has
// no such column-level immutability — if the incoming MutableRecordBuffer's SystemId
// slot were ever wrong at Modify-time (reset, stale, or otherwise not matching the
// stored row), Modify would silently write that wrong value into the store and
// GetBySystemId would stop finding the row. No normal AL statement sequence
// reproduces a wrong incoming value today (checked: plain field mutation, and
// Get()-then-Modify, both keep the buffer's SystemId slot correct — see the corpus
// regression tests this fix ships with), but the guard below is cheap, exactly
// mirrors what real BC's SQL layer does unconditionally, and closes the mechanism
// the fork report (credited in #2573) identified — not just the one AL shape that
// happened to trigger it there.
//
// ── NOT covered here: TempTableDataProvider.ModifyAll (issue #2644) ─────────────
//
// AL's Record.ModifyAll routes through a THIRD provider method, not the two Cecil
// prepends this file adds to (Insert/Modify). Its body has the identical shape --
// `row[item3.Key] = item3.Value` for every field in the update dictionary, no
// SystemId exclusion (decompiled, BC 28.1 Ncl.dll) -- and a local compile probe
// confirms `Rec.ModifyAll(SystemId, NewGuidValue)` is accepted, not rejected, so
// it is reachable from ordinary AL. Left as a follow-up rather than fixed here:
// ModifyAll has no existing prepend hook, and adding one needs
// AlRunner/Infrastructure/NclCecilRewrite.cs, which was locked by an in-flight PR
// while this fix was being written. See #2644.
//
// ── Why this is observably equivalent to real BC (loud-failures.md audit) ────────
//
// Both halves are gated the same way rowversion stamping is (BlobStoreIsolationPatches
// .IsDatabaseBacked) — a `temporary` record's SystemId is a plain in-memory field on
// real BC with no DB-level constraint, so neither guard runs for one. The reflection
// lookups below follow the SAME loud-failure convention as RowVersionPatches.cs: a
// resolution failure throws InvalidOperationException naming the missing member,
// never silently reverts to "no check" / "no restore" (see that file's header for the
// full rationale — RunnerOutOfScopeException does not apply to an internal-invariant
// break like this). The one legitimate quiet path mirrors TimestampField's: a table
// with no SystemId field (companion-table shapes) truthfully has nothing to check.
//
// ── The BC-own exception factory reused for the duplicate-SystemId error ─────────
//
// BC's own TempTableDataProvider.Insert raises a duplicate AL-unique-key violation
// through RecordImplementationHelper.GetUniqueConstraintException, which formats
// Lang.UniqueIndexError ("There is already a record in table {0} that has the same
// values in a unique index for the following fields: {1}", confirmed identical in
// Microsoft.Dynamics.Nav.Language.dll on BC 27.0 (live container), 27.5 and 28.3
// (decompiled)) via NavCSideDuplicateKeyException.CreateUniqueConstraint(string, string)
// — a PUBLIC static factory. This patch calls that same factory directly for the
// SystemId case, so AL sees BC's own message rather than a runner paraphrase. The
// factory type is resolved by scanning loaded assemblies (not a direct compile-time
// reference), matching ALDatabasePatches.ResolveNavCSideExceptionType's established
// precedent for building a BC exception type safely regardless of assembly-identity
// quirks between Microsoft.Dynamics.Nav.Types and the dynamically loaded Ncl.dll.
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RowVersionPatches
{
    private static PropertyInfo? _pSystemIdField;         // NCLMetaTable.SystemIdField (internal)
    private static PropertyInfo? _pSystemIdProp;          // MutableRecordBuffer.SystemId (internal, get-only)
    private static PropertyInfo? _pReadOnlyBuffer;         // MutableRecordBuffer.ReadOnlyBuffer (internal)
    private static PropertyInfo? _pReadOnlyBufferSystemId; // ReadOnlyRecordBuffer.SystemId (public, but the
                                                            // declaring type MutableRecordBuffer.ReadOnlyBuffer
                                                            // resolves to is only known by its runtime Type)
    private static PropertyInfo? _pTableCaptionSafe;      // NCLMetaTable.TableCaptionSafe (internal)
                                                            // row's own runtime Type — kept as reflection (not a
                                                            // direct cast) so this whole mechanism is unit-testable
                                                            // with reflected-shape fakes, matching this file's and
                                                            // RowVersionPatchesTests.cs's established convention.
    private static FieldInfo? _fPrimaryTree;              // TempTableDataProvider.primaryTree (internal, private)
    private static MethodInfo? _mCreateUniqueConstraint;  // NavCSideDuplicateKeyException.CreateUniqueConstraint

    /// <summary>
    /// Resolves the FieldIndex of the table's SystemId field, or null when the table
    /// genuinely has none (the one legitimate quiet path — mirrors Stamp()'s
    /// TimestampField handling). Throws on any other resolution failure.
    /// </summary>
    private static int? ResolveSystemIdFieldIndex(object metaTable)
    {
        _pSystemIdField ??= metaTable.GetType().GetProperty("SystemIdField",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"[RowVersionPatches] {metaTable.GetType().Name}.SystemIdField property not found — " +
                "SystemId integrity check cannot resolve its reflection target");
        var systemIdField = _pSystemIdField.GetValue(metaTable);
        if (systemIdField == null) return null; // table genuinely has no SystemId field

        _pFieldIndex ??= systemIdField.GetType().GetProperty("FieldIndex",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"[RowVersionPatches] {systemIdField.GetType().Name}.FieldIndex property not found — " +
                "SystemId integrity check cannot resolve its reflection target");
        return (int)_pFieldIndex.GetValue(systemIdField)!;
    }

    private static object ResolveMetaTable(object recordBuffer, Type bufferType)
    {
        _pMetaTable ??= bufferType.GetProperty("MetaTable",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"[RowVersionPatches] {bufferType.Name}.MetaTable property not found — " +
                "SystemId integrity check cannot resolve its reflection target");
        return _pMetaTable.GetValue(recordBuffer)
            ?? throw new InvalidOperationException(
                "[RowVersionPatches] record buffer has no MetaTable");
    }

    /// <summary>
    /// Per-row-type compiled getter for the stored row's SystemId. PropertyInfo.GetValue
    /// costs roughly two orders of magnitude more than a delegate call, and this runs once
    /// per stored row per insert, so the difference decides whether a bulk materialisation
    /// finishes or times out. Keyed by concrete row type because the store holds one type
    /// in practice but nothing in the surrounding code guarantees it.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Func<object, NavGuid>>
        _rowSystemIdGetters = new();

    private static NavGuid ReadRowSystemId(object rowObj)
    {
        var getter = _rowSystemIdGetters.GetOrAdd(rowObj.GetType(), static t =>
        {
            var prop = t.GetProperty("SystemId",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException(
                    $"[RowVersionPatches] {t.Name}.SystemId property not found — " +
                    "SystemId integrity check cannot resolve its reflection target");
            var getMethod = prop.GetGetMethod(nonPublic: true)
                ?? throw new InvalidOperationException(
                    $"[RowVersionPatches] {t.Name}.SystemId has no getter");
            var instance = System.Linq.Expressions.Expression.Parameter(typeof(object), "row");
            var body = System.Linq.Expressions.Expression.Call(
                System.Linq.Expressions.Expression.Convert(instance, t), getMethod);
            return System.Linq.Expressions.Expression
                .Lambda<Func<object, NavGuid>>(body, instance).Compile();
        });
        return getter(rowObj);
    }

    /// <summary>
    /// Cecil-prepend body (called from OnBeforeInsert): refuse a second Insert whose
    /// explicit SystemId already exists on this database-backed table, matching real
    /// BC's SQL unique-constraint violation on $systemId.
    /// </summary>
    // ── The runner's own snapshot replay is not an AL Insert (issue #2694) ──────────
    //
    // RecordPatches.RollbackToCommitPoint restores a table by clearing it and re-inserting the
    // snapshot rows THROUGH THIS SAME provider method, so every restored row arrives here
    // carrying the SystemId it already had. Real BC's rollback is a transaction abort — it never
    // issues an INSERT — so there is no unique constraint for a replay to violate, and applying
    // the guard there is a category error rather than a stricter reading of it.
    //
    // It was also total: on Microsoft's Tests-SINGLESERVER (BC 28.1) the restore threw and the
    // bucket ran 0 of 878 tests, bisected to #2639's commit against its parent d9f01ca1. A
    // restore that throws part way is worse than either outcome it chooses between — the table
    // keeps some of the snapshot and none of the rest.
    //
    // Scoped and per-thread rather than a global off switch: AL test bodies run on their own
    // thread (TestExecutor.InvokeWithTimeout), and a process-wide flag would let one thread's
    // restore disable a genuine AL Insert's check on another.
    [ThreadStatic] private static bool _suppressSystemIdUniqueness;

    /// <summary>True while this thread is replaying a snapshot rather than running AL Insert.</summary>
    public static bool IsSystemIdUniquenessSuppressed => _suppressSystemIdUniqueness;

    /// <summary>
    /// Suppress the duplicate-SystemId refusal for the duration of a snapshot replay. Restores
    /// the ENCLOSING state on dispose, not unconditionally false, so a rollback nested inside a
    /// rollback cannot re-arm the check for the outer replay's remaining rows.
    /// </summary>
    public static IDisposable SuppressSystemIdUniqueness() => new SystemIdSuppressionScope();

    private sealed class SystemIdSuppressionScope : IDisposable
    {
        private readonly bool _previous;
        public SystemIdSuppressionScope()
        {
            _previous = _suppressSystemIdUniqueness;
            _suppressSystemIdUniqueness = true;
        }
        public void Dispose() => _suppressSystemIdUniqueness = _previous;
    }

    /// <summary>Per-store SystemId indexes (#2667). Keyed on the provider so an index dies with
    /// the store it describes; a table the run never touches again costs nothing.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, StoredSystemIdIndex>
        _systemIdIndexes = new();

    /// <summary>The AvlTree row count, in O(1), or -1 when the tree cannot answer cheaply.
    /// BC's own <c>AvlTree&lt;T&gt;.CountIfBounded</c> returns the node count for an unbounded
    /// tree (which <c>primaryTree</c> always is) and -1 for a bounded view.</summary>
    private static int TryGetStoredRowCount(object storedRows)
    {
        var getter = _treeCountGetters.GetOrAdd(storedRows.GetType(), static t =>
        {
            var prop = t.GetProperty("CountIfBounded",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var getMethod = prop?.GetGetMethod(nonPublic: true);
            if (getMethod == null || prop!.PropertyType != typeof(int)) return null;
            var instance = System.Linq.Expressions.Expression.Parameter(typeof(object), "tree");
            var body = System.Linq.Expressions.Expression.Call(
                System.Linq.Expressions.Expression.Convert(instance, t), getMethod);
            return System.Linq.Expressions.Expression.Lambda<Func<object, int>>(body, instance).Compile();
        });
        // No such property on this BC build: fall back to the scan rather than guess a count.
        return getter == null ? -1 : getter(storedRows);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Func<object, int>?>
        _treeCountGetters = new();

    private static void CheckNoDuplicateSystemId(object? provider, object? recordBuffer)
    {
        if (_suppressSystemIdUniqueness)
        {
            // A snapshot replay writes rows straight past this check (#2694), so anything this
            // index believes about that store is now guesswork. Drop it; the next real insert
            // rebuilds. See StoredSystemIdIndex for why a stale entry is the dangerous direction.
            if (provider != null && _systemIdIndexes.TryGetValue(provider, out var replayed))
                lock (replayed) replayed.Invalidate();
            return;
        }
        if (recordBuffer == null || !BlobStoreIsolationPatches.IsDatabaseBacked(provider)) return;

        var bufferType = recordBuffer.GetType();
        var metaTable = ResolveMetaTable(recordBuffer, bufferType);

        // Virtual/system tables (2000000000 and up) are computed per request and
        // materialised into a temp store; their rows never reach SQL, so real BC has no
        // $systemId unique constraint to violate on them and this check has nothing
        // faithful to enforce. Skipping them is also what makes on-demand materialisation
        // affordable: the scan below is O(stored rows) per insert, and the Date virtual
        // table (2000000007) widens its window by bulk-inserting, which turned a 908 ms
        // suite into a 60 s watchdog timeout with the check applied
        // (tests/runner-extras/date-virtual-table-window, Codeunit64561, measured on
        // BC 28.4). See #2667 for the residual O(rows^2) on a genuinely large real table.
        if (metaTable is NCLMetaTable ncl && ncl.TableId >= 2000000000) return;

        var systemIdIndex = ResolveSystemIdFieldIndex(metaTable);
        if (systemIdIndex == null) return; // no SystemId field on this table — nothing to check

        _pSystemIdProp ??= bufferType.GetProperty("SystemId",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"[RowVersionPatches] {bufferType.Name}.SystemId property not found — " +
                "SystemId integrity check cannot resolve its reflection target");
        var incomingSystemId = (NavGuid)_pSystemIdProp.GetValue(recordBuffer)!;
        // A zero/empty incoming SystemId means the AL statement supplied none — the
        // UUID-generation hook (SequentialUuidCreator, Cecil-owned — see
        // RecordWritePatches.cs header) assigns a fresh, always-unique value before
        // the real Insert body runs, so there is nothing yet to collide with.
        if (incomingSystemId.IsZeroOrEmpty) return;

        if (provider == null) return;
        // primaryTree is PRIVATE on TempTableDataProvider, and GetField(NonPublic) on a
        // derived type does not return a base class's private fields — BC's own
        // CrmTableConnection.CrmTestDataProvider (the '@@test@@' CRM test connection, #2725)
        // derives from it. PrivateMemberLookup walks the hierarchy asking each level for its
        // OWN declarations, which is right for the exact type AND for a derived one; see that
        // class for why climbing to a type of a KNOWN NAME instead is wrong.
        if (!AlRunner.Infrastructure.PrivateMemberLookup.FitsInstance(_fPrimaryTree, provider))
            _fPrimaryTree = AlRunner.Infrastructure.PrivateMemberLookup
                .Field(provider.GetType(), "primaryTree")
                ?? throw new InvalidOperationException(
                    $"[RowVersionPatches] {provider.GetType().Name}.primaryTree field not found — " +
                    "SystemId integrity check cannot resolve its reflection target");
        // A NULL primaryTree means no rows are stored yet for this table instance
        // (EnsureTreeCreated has not run, which happens INSIDE the real Insert body this
        // prepend runs ahead of) — nothing to collide with, so a quiet return is right.
        //
        // A NON-NULL value the runner cannot enumerate is the same "BC's private layout
        // moved" case the resolution above already refuses, and folding it into the null
        // branch silently skipped the duplicate-SystemId check on every insert (#2786).
        var storedTree = _fPrimaryTree.GetValue(provider);
        if (storedTree == null) return;
        if (storedTree is not System.Collections.IEnumerable storedRows)
            throw new InvalidOperationException(
                $"[RowVersionPatches] {provider.GetType().Name}.primaryTree holds a " +
                $"{storedTree.GetType().Name}, which cannot be enumerated — " +
                "SystemId integrity check cannot read the stored rows");

        // #2667: answer from a per-store index instead of walking every stored row. The walk
        // ran on EVERY insert into a database-backed table — note that the zero-SystemId early
        // return above does NOT spare the common case, because the id is already assigned by the
        // time this runs — making it O(rows) per insert and O(rows^2) over a bulk load. Measured
        // on a 3-field table with the guard bypassed as the control, it was 93-97% of all the
        // work an insert did. See StoredSystemIdIndex for the invalidation rules.
        var storedRowCount = TryGetStoredRowCount(storedRows);
        if (storedRowCount < 0)
        {
            // The store cannot give a cheap, trustworthy count, so an index built from it could
            // not be verified. Fall back to exactly the pre-#2667 walk rather than risk a stale
            // entry refusing an insert real BC would accept.
            foreach (var rowObj in storedRows)
            {
                if (ReadRowSystemId(rowObj).Value != incomingSystemId.Value) continue;
                throw BuildDuplicateSystemIdException(
                    ResolveTableCaptionSafe(metaTable), $"SystemId={incomingSystemId.Value}");
            }
            return;
        }

        var index = _systemIdIndexes.GetOrCreateValue(provider);
        lock (index)
        {
            // Deciding to rebuild, looking the id up, and noting the row this call is about to
            // clear have to be one atomic step: two threads inserting into the same store must
            // not both see "not present" for the same id.
            index.SyncTo(storedRowCount, () => EnumerateStoredSystemIds(storedRows));
            if (index.Contains(incomingSystemId.Value))
                throw BuildDuplicateSystemIdException(
                    ResolveTableCaptionSafe(metaTable), $"SystemId={incomingSystemId.Value}");
            index.NoteInserting(incomingSystemId.Value, storedRowCount);
        }
    }

    /// <summary>Every stored row's SystemId, read through the same compiled per-row-type getter
    /// the pre-#2667 walk used. Lazy, so a sync that does not rebuild never touches a row.</summary>
    private static IEnumerable<Guid> EnumerateStoredSystemIds(System.Collections.IEnumerable storedRows)
    {
        foreach (var rowObj in storedRows) yield return ReadRowSystemId(rowObj).Value;
    }

    /// <summary>
    /// Cecil-prepend body (called from OnBeforeModify): force the incoming buffer's
    /// SystemId slot back to the row's current stored value before BC's own Modify
    /// body runs its unconditional per-field copy (see file header). Restores
    /// silently rather than throwing — real BC does not error on a changed SystemId
    /// either, the SQL UPDATE simply never touches that column, so the change is
    /// just ignored.
    /// </summary>
    private static void PreserveSystemIdOnModify(object? provider, object? recordBuffer)
    {
        if (recordBuffer == null || !BlobStoreIsolationPatches.IsDatabaseBacked(provider)) return;

        var bufferType = recordBuffer.GetType();
        var metaTable = ResolveMetaTable(recordBuffer, bufferType);
        var systemIdIndex = ResolveSystemIdFieldIndex(metaTable);
        if (systemIdIndex == null) return; // no SystemId field on this table — nothing to preserve

        _pReadOnlyBuffer ??= bufferType.GetProperty("ReadOnlyBuffer",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"[RowVersionPatches] {bufferType.Name}.ReadOnlyBuffer property not found — " +
                "SystemId integrity check cannot resolve its reflection target");
        var readOnlyBufferObj = _pReadOnlyBuffer.GetValue(recordBuffer)
            ?? throw new InvalidOperationException(
                "[RowVersionPatches] record buffer has no ReadOnlyBuffer — cannot preserve its SystemId across Modify");
        _pReadOnlyBufferSystemId ??= readOnlyBufferObj.GetType().GetProperty("SystemId",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"[RowVersionPatches] {readOnlyBufferObj.GetType().Name}.SystemId property not found — " +
                "SystemId integrity check cannot resolve its reflection target");
        var storedSystemId = (NavGuid)_pReadOnlyBufferSystemId.GetValue(readOnlyBufferObj)!;
        if (storedSystemId.IsZeroOrEmpty) return; // defensive: an existing row should always carry one

        _pItem ??= bufferType.GetProperty("Item",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"[RowVersionPatches] {bufferType.Name}.Item indexer not found — " +
                "SystemId integrity check cannot resolve its reflection target");
        var incomingSystemId = (NavGuid)_pItem.GetValue(recordBuffer, new object[] { systemIdIndex.Value })!;
        if (incomingSystemId.Value == storedSystemId.Value) return; // already correct — the common case

        _pItem.SetValue(recordBuffer, storedSystemId, new object[] { systemIdIndex.Value });
    }

    private static string ResolveTableCaptionSafe(object metaTable)
    {
        _pTableCaptionSafe ??= metaTable.GetType().GetProperty("TableCaptionSafe",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"[RowVersionPatches] {metaTable.GetType().Name}.TableCaptionSafe property not found — " +
                "SystemId integrity check cannot resolve its reflection target");
        return (_pTableCaptionSafe.GetValue(metaTable) as string) ?? string.Empty;
    }

    /// <summary>
    /// Build BC's own NavCSideDuplicateKeyException via its public
    /// CreateUniqueConstraint(string, string) factory (Microsoft.Dynamics.Nav.Types),
    /// resolved by scanning loaded assemblies rather than a direct compile-time
    /// reference — see file header for why. Falls back to BC's own known en-US
    /// message text (confirmed on BC 27.0/27.5/28.3, see file header) wrapped in a
    /// plain InvalidOperationException if the factory cannot be resolved or invoked:
    /// AL must still see an error here, because BC would have thrown one — never a
    /// silent accept of the duplicate.
    /// </summary>
    private static Exception BuildDuplicateSystemIdException(string tableCaption, string fieldsAndValues)
    {
        try
        {
            if (_mCreateUniqueConstraint == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = asm.GetType("Microsoft.Dynamics.Nav.Types.Exceptions.NavCSideDuplicateKeyException",
                        throwOnError: false);
                    _mCreateUniqueConstraint = t?.GetMethod("CreateUniqueConstraint",
                        BindingFlags.Public | BindingFlags.Static, binder: null,
                        new[] { typeof(string), typeof(string) }, modifiers: null);
                    if (_mCreateUniqueConstraint != null) break;
                }
            }
            if (_mCreateUniqueConstraint != null)
                return (Exception)_mCreateUniqueConstraint.Invoke(null, new object[] { tableCaption, fieldsAndValues })!;
        }
        catch
        {
            // Fall through to the hardcoded fallback below.
        }

        return new InvalidOperationException(string.Format(
            "There is already a record in table {0} that has the same values in a unique index for the following fields: {1}",
            tableCaption, fieldsAndValues));
    }
}
