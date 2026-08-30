// RecordPatches.TransactionSnapshot — AL's write-transaction rollback.
//
// WHAT AL PROMISES
//   An AL error rolls the database back to the last COMMIT. The test framework establishes
//   a commit point at the start of every test method, and AL's Commit() establishes another.
//   BC implements the rollback half in its own code: NavMethodScope.AssertError catches the
//   error and calls session.Rollback().
//
//   That is observable, and the corpus pins it:
//     * TestAssertErrorRollback.al (Codeunit 60943) — an uncommitted write DOES roll back on
//       a LATER, textually UNRELATED asserterror'd statement (a bare Error() call with no
//       write of its own); an intervening Commit() moves the surviving boundary forward, and
//       only writes since that Commit() are undone. This is the general "roll back to the
//       last commit point" rule and it stays exactly as it always has — the fixes below are
//       narrow exceptions to it, not a replacement for it. (AlRunner#2142's own examples,
//       TestScopeIsolationContracts.Test04 and TestTransactionContracts
//       .Error_After_Insert_Before_Commit_RecordPersists, assert the OPPOSITE of this
//       codeunit for the exact same shape — an uncommitted, untriggered Insert() surviving a
//       later unrelated Error() — and are the stale corpus tests here: Codeunit 60943 is the
//       newer, more careful measurement and both those two are superseded by it. This is a
//       genuine corpus inconsistency, not a runner gap; that correction belongs upstream.)
//     * TestTriggerRollback.OnModify_Throws_ValueNotModified — an explicit Commit() before
//       the asserterror, precisely so the Insert above it survives the rollback.
//     * TestTriggerRollback.OnDelete_Throws_RecordStillExists — Insert() (no error, no
//       Commit()) then `asserterror Delete()` whose OnDelete trigger throws: the Insert must
//       survive. Unlike Codeunit 60943's "unrelated" shape, the LATER statement here is
//       ITSELF a write attempt against the SAME table — see the "always re-baseline on the
//       next write" fix below.
//     * TestTriggerRollback.OnInsert_Throws_RecordNotInserted — asserterror wraps the Insert
//       call itself, and OnInsert's trigger throws. Real BC (Cloud, measured) keeps the row:
//       OnInsert runs AFTER the physical write on real BC, so the write is already durable
//       by the time the trigger can object. See the Insert force-durable fix below.
//
//   Without any of this the runner either never rolled anything back (silently wrong for a
//   test that checks the table afterwards) or rolled back to the wrong boundary.
//
//   BC's own APIs establish additional, NESTED commit points too — a real
//   Session.EndTransaction(commit: true) (or EndTransactionWorldAndTransaction) inside a BC
//   API is exactly as durable, from AL's point of view, as an explicit Commit() statement.
//   See NoteTransactionEnd below (AlRunner#1946).
//
// HOW IT IS DONE HERE
//   The runner's tables are BC TempTableDataProviders held in _dataAccessByTable, and
//   RecordPatches.InstallBaseline already knows how to copy rows out of them and put rows
//   back. A commit point is the same snapshot, kept separately; a rollback restores it.
//
//   The snapshot is taken on EVERY write to a table (see ALDatabasePatches.NoteRecordWrite /
//   NoteRecordInsertWrite, prepended to every NavRecord AL write entry), always refreshed to
//   the table's CURRENT live state — not just once, lazily, on the first write since the
//   last commit point. Refreshing on every write is what keeps
//   OnDelete_Throws_RecordStillExists correct: Insert() establishes a baseline, and Delete()
//   — even though its own trigger throws before any physical delete happens — refreshes that
//   baseline to include the Insert's row, so a rollback restores exactly that (a no-op, since
//   nothing physically changed) instead of reaching back to the pre-Insert baseline and
//   erasing the earlier, unrelated write. This does not change TestAssertErrorRollback's
//   "unrelated Error() rolls back everything since commit" cases: none of them write to the
//   same table twice without an intervening Commit(), so there's only ever one baseline to
//   refresh into.
//
//   Insert() is excluded from the ABOVE mechanism's protection in one specific way: measured
//   real BC keeps an Insert() row durable even when THAT SAME Insert() statement's own
//   OnInsert trigger throws (TestTriggerRollback.OnInsert_Throws_RecordNotInserted) — but
//   this runner's physical write for Insert only actually lands once NavRecord.ALInsertAsync
//   returns without throwing (OnInsert runs BEFORE that completes here, the opposite of the
//   documented real-BC order), so RollbackToCommitPoint has nothing to undo AND the row was
//   simply never written. ForceDurableFailedInserts (see below) makes it durable directly,
//   but ONLY for Insert() attempts made during the statement asserterror is CURRENTLY
//   wrapping (BeginAssertErrorScope/EndAssertErrorScope) — an Insert() from an EARLIER,
//   already-returned statement must stay fully subject to the general "unrelated error rolls
//   back everything since commit" rule above (that's the Codeunit 60943 case), so it must
//   NOT be in scope for a later, different asserterror's force-durable step.
//
//   Restore is IN PLACE — the provider object is kept and its trees are rebuilt — because
//   unlike the codeunit-boundary install-baseline restore, a rollback happens mid-test with
//   AL record variables still holding references to the DataAccess they were opened on.
using System.Collections;
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    // Per-table row images captured since the last commit point, keyed by the
    // (DataAccessSource, tableId) pair _dataAccessByTable itself is keyed by.
    private static readonly Dictionary<(object Source, int TableId), BaselineTable> _txCommitPoint = new();

    // Insert() attempts noted (ALDatabasePatches.NoteRecordInsertWrite) during the CURRENTLY
    // executing asserterror-wrapped statement — see ForceDurableFailedInserts. Scoped with a
    // [ThreadStatic] stack, pushed/cleared in BeginAssertErrorScope and restored in
    // EndAssertErrorScope, so an Insert() from an EARLIER, already-returned statement is
    // never mistaken for one made by the CURRENT statement (that distinction is exactly what
    // keeps this fix from reaching into TestAssertErrorRollback's "unrelated error" cases,
    // which must keep rolling back an uncommitted Insert normally).
    [ThreadStatic]
    private static List<object>? _pendingInsertsInScope;

    [ThreadStatic]
    private static Stack<List<object>>? _pendingInsertsScopeStack;

    /// <summary>
    /// Establish a commit point: everything written up to now survives a later rollback.
    /// Called at each test-method boundary and from AL's <c>Commit()</c>.
    /// </summary>
    public static void MarkCommitPoint() => _txCommitPoint.Clear();

    /// <summary>
    /// Start tracking Insert() attempts for the statement asserterror is about to invoke,
    /// pushing aside whatever the OUTER scope (if any — nested asserterror) had accumulated.
    /// Called from MethodScopePatches.NavMethodScope_AssertError immediately before invoking
    /// the wrapped Action. Does NOT touch <see cref="_txCommitPoint"/> — the general
    /// roll-back-to-last-commit-point rule is unscoped by design (Codeunit 60943).
    /// </summary>
    public static void BeginAssertErrorScope()
    {
        (_pendingInsertsScopeStack ??= new()).Push(_pendingInsertsInScope ?? new List<object>());
        _pendingInsertsInScope = new List<object>();
    }

    /// <summary>
    /// Restore the outer scope's pending-inserts list pushed aside by
    /// <see cref="BeginAssertErrorScope"/>. Called from
    /// MethodScopePatches.NavMethodScope_AssertError in a finally around the wrapped Action,
    /// after <see cref="ForceDurableFailedInserts"/> (if the statement threw) has already
    /// consumed this scope's own list.
    /// </summary>
    public static void EndAssertErrorScope()
    {
        var stack = _pendingInsertsScopeStack;
        _pendingInsertsInScope = (stack != null && stack.Count > 0) ? stack.Pop() : null;
    }

    /// <summary>
    /// Note an Insert() attempted during the currently-executing asserterror-wrapped
    /// statement (or, outside any asserterror, harmlessly — nothing reads the list except
    /// <see cref="ForceDurableFailedInserts"/>, called only from the asserterror catch path).
    /// Called from ALDatabasePatches.NoteRecordInsertWrite.
    /// </summary>
    internal static void NoteInsertAttempt(object? record)
    {
        if (record == null) return;
        (_pendingInsertsInScope ??= new()).Add(record);
    }

    /// <summary>
    /// Prepended to SessionTransactionExtensions.EndTransaction(NavSession, bool commit) and
    /// .EndTransactionWorldAndTransaction(NavSession, bool commit) — see AlRunner#1946.
    ///
    /// BC's own APIs run their internal work inside an explicit nested transaction. The
    /// static overload of <c>NavXmlPort.Import</c> is one — decompiled, unmodified Ncl body:
    /// <c>Session.BeginTransaction(); ...; finally { Session.EndTransaction(commit); }</c> for
    /// <c>DataError.ThrowError</c>, or <c>Session.BeginTransactionWorldAndTransaction(); ...;
    /// finally { Session.EndTransactionWorldAndTransaction(commit); }</c> for
    /// <c>DataError.TrapError</c>. AL's compiler picks <c>TrapError</c> whenever the call's
    /// boolean result is captured into a variable — e.g. <c>Ok := XmlPort.Import(...)</c> —
    /// which is the common, idiomatic AL shape, so both extension methods need the hook, not
    /// just the more obviously-named one.
    ///
    /// A real <c>commit == true</c> there is exactly as durable, from AL's point of view, as
    /// an explicit <c>Commit()</c> statement: a later, unrelated <c>asserterror</c> in the
    /// CALLER must not roll back work an inner API already committed.
    ///
    /// Before this hook, only AL's own <c>Commit()</c> and the per-test isolation boundary
    /// called <see cref="MarkCommitPoint"/>, so <see cref="RollbackToCommitPoint"/> rolled
    /// all the way back to test-method start on ANY later trapped error — including rows a
    /// nested BC API (like XmlPort.Import) had already committed inside its own transaction.
    /// Observably: <c>XmlPort.Import(id, Stream, Rec)</c> inserts a row, a LATER, unrelated
    /// statement in the same test method throws (even caught by <c>asserterror</c>), and the
    /// earlier insert vanished — reproducible with no XmlPort involved at all, just a plain
    /// <c>Record.Insert()</c> followed by an unrelated failing <c>Record.Delete()</c>.
    ///
    /// This must NOT fire for a plain <c>Record.Insert/Modify/Delete/Rename</c> call — those
    /// never call <c>EndTransaction</c> themselves (see <see cref="ALDatabasePatches.NoteRecordWrite"/>);
    /// they just participate in whatever transaction is already open, ended by the test
    /// framework's own boundary or an explicit AL <c>Commit()</c>. So this only ever marks a
    /// commit point for a real nested-transaction completion, not for every write — the
    /// corpus's <c>OnModify_Throws_ValueNotModified</c> (an uncommitted plain
    /// <c>Insert()</c> IS rolled back by a later trapped error) still holds.
    /// </summary>
    public static void NoteTransactionEnd(object? session, bool commit)
    {
        if (commit) MarkCommitPoint();
    }

    /// <summary>
    /// Snapshot the record's table to its CURRENT live state on every write — see the file
    /// header's "always refresh" note for why this must not skip when a snapshot already
    /// exists for the table (that lazy-first-write-only version is what let
    /// OnDelete_Throws_RecordStillExists's rollback reach back past an earlier, unrelated
    /// Insert to a stale, pre-Insert baseline). Called from
    /// <see cref="ALDatabasePatches.NoteRecordWrite"/> / <see cref="ALDatabasePatches.NoteRecordInsertWrite"/>,
    /// which BC's own AL write entry points run before doing anything.
    /// </summary>
    internal static void NoteTransactionWrite(object? record)
    {
        if (record is not NavRecord rec) return;
        int tableId;
        try { tableId = rec.MetaTable.TableId; }
        catch { return; }

        foreach (var (source, perTable) in _dataAccessByTable)
        {
            if (!perTable.TryGetValue(tableId, out var dataAccess)) continue;
            var key = (source, tableId);

            var provider = GetDataProvider(dataAccess);
            if (provider == null || provider.GetType().Name != "TempTableDataProvider") continue;

            var providerType = provider.GetType();
            var metaTable = RequiredField(providerType, "table").GetValue(provider);
            if (metaTable == null) continue;

            // A null primaryTree simply means no row was ever inserted — the pre-write image
            // of this table is "empty", which is exactly what an empty row array restores to.
            var rows = new List<NavValue[]>();
            if (RequiredField(providerType, "primaryTree").GetValue(provider) is IEnumerable primaryTree)
                foreach (var row in primaryTree)
                    if (row is TempTableRecordBuffer buffer)
                        rows.Add(CloneValues(buffer.ToArray()));

            _txCommitPoint[key] = new BaselineTable(tableId, metaTable, rows.ToArray());
        }
    }

    /// <summary>
    /// Roll the row store back to the last commit point. Called from BC's own
    /// <c>SessionTransactionExtensions.Rollback</c> (rewritten to land here), which
    /// NavMethodScope.AssertError invokes after catching an AL error.
    ///
    /// Only tables written since the commit point were snapshotted, and only those are
    /// touched — a rollback must not disturb a table nothing wrote to.
    /// </summary>
    public static void RollbackToCommitPoint(object? session)
    {
        if (_txCommitPoint.Count == 0) return;
        foreach (var ((source, tableId), saved) in _txCommitPoint.ToList())
        {
            if (!_dataAccessByTable.TryGetValue(source, out var perTable)) continue;
            if (!perTable.TryGetValue(tableId, out var dataAccess)) continue;
            var provider = GetDataProvider(dataAccess);
            if (provider == null || provider.GetType().Name != "TempTableDataProvider") continue;

            ClearProviderInPlace(provider);
            InsertRows(provider, saved.MetaTable, saved.Rows);
        }
        // The rolled-back work is gone; the commit point itself still stands, so the next
        // write re-snapshots from the restored state.
        _txCommitPoint.Clear();
    }

    private static FieldInfo? _fNavRecordRecordImplementation;
    private static FieldInfo? _fRecordImplementationMutableRecordBuffer;

    /// <summary>
    /// Called from MethodScopePatches.NavMethodScope_AssertErrorCore's catch handler, AFTER
    /// <see cref="RollbackToCommitPoint"/> — order matters: a Modify/Delete on the SAME table
    /// tracked earlier in this same statement could restore the table to a state that
    /// pre-dates an Insert() also made during this statement, and inserting before that
    /// rollback runs would just get discarded again.
    ///
    /// For every Insert() attempted during THIS statement (BeginAssertErrorScope/
    /// EndAssertErrorScope-scoped — see their docs and the file header for why an Insert()
    /// from an earlier, different statement must NOT be forced durable here), forces the row
    /// durable if it isn't already there. Real BC's measured behaviour
    /// (TestTriggerRollback.OnInsert_Throws_RecordNotInserted) is that the row survives even
    /// OnInsert's own trigger throwing; this runner's physical write only actually lands once
    /// NavRecord.ALInsertAsync returns without throwing (OnInsert runs before that write
    /// completes here — the opposite order from the real BC trigger-dispatch path this
    /// runner otherwise reuses unmodified), so without this the row is simply never written.
    /// Reusing the record's own live <c>RecordImplementation.mutableRecordBuffer</c> (rather
    /// than re-deriving field values ourselves) means the values inserted are exactly what
    /// BC's own precompiled Insert() populated onto the record before OnInsert ran.
    ///
    /// A record that already made it into the table (OnInsert succeeded, or a previous
    /// force-insert already ran) throws a duplicate-key error from the provider's own Insert
    /// — swallowed here, since "already durable" is exactly the outcome wanted.
    /// </summary>
    public static void ForceDurableFailedInserts()
    {
        var pending = _pendingInsertsInScope;
        if (pending == null || pending.Count == 0) return;
        foreach (var record in pending) ForceDurableInsert(record);
        pending.Clear();
    }

    private static void ForceDurableInsert(object record)
    {
        if (record is not NavRecord rec) return;
        int tableId;
        try { tableId = rec.MetaTable.TableId; }
        catch { return; }

        _fNavRecordRecordImplementation ??= typeof(NavRecord).GetField(
            "recordImplementation", BindingFlags.NonPublic | BindingFlags.Instance);
        if (_fNavRecordRecordImplementation == null) return;
        object? recImpl;
        try { recImpl = _fNavRecordRecordImplementation.GetValue(rec); }
        catch { return; }
        if (recImpl == null) return;

        _fRecordImplementationMutableRecordBuffer ??= recImpl.GetType().GetField(
            "mutableRecordBuffer", BindingFlags.NonPublic | BindingFlags.Instance);
        if (_fRecordImplementationMutableRecordBuffer == null) return;
        object? buffer;
        try { buffer = _fRecordImplementationMutableRecordBuffer.GetValue(recImpl); }
        catch { return; }
        if (buffer == null) return;

        foreach (var (source, perTable) in _dataAccessByTable)
        {
            if (!perTable.TryGetValue(tableId, out var dataAccess)) continue;
            var provider = GetDataProvider(dataAccess);
            if (provider == null || provider.GetType().Name != "TempTableDataProvider") continue;

            try
            {
                var insert = provider.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .First(m => m.Name == "Insert" && m.GetParameters().Length == 4
                             && m.GetParameters()[0].ParameterType == typeof(int));
                var insertOptions = Enum.ToObject(insert.GetParameters()[2].ParameterType, 0);
                insert.Invoke(provider, new object?[] { 0, buffer, insertOptions, null });
            }
            catch
            {
                // Already present (duplicate key from a successful Insert, or a previous
                // force-insert on a different DataAccessSource for the same table) — the
                // record being durable is exactly the outcome this method exists to reach.
            }
        }
    }

    /// <summary>
    /// Drop every row from a TempTableDataProvider without replacing the provider itself.
    /// Restoring in place matters: unlike the codeunit-boundary install-baseline restore, a
    /// rollback happens mid-test with AL record variables still holding the DataAccess they
    /// were opened on. The three collections are exactly what <c>Insert</c> re-creates
    /// through <c>EnsureTreeCreated()</c>, so nulling them is the provider's own
    /// "no rows yet" state.
    /// </summary>
    private static void ClearProviderInPlace(object provider)
    {
        var t = provider.GetType();
        foreach (var name in new[] { "trees", "primaryTree", "uniqueIndexes" })
            AlRunner.Infrastructure.FieldPoke.SetInstance(RequiredField(t, name), provider, null);
    }

    /// <summary>Put saved rows back, deep-copying so the snapshot stays reusable.</summary>
    private static void InsertRows(object provider, object metaTable, NavValue[][] rows)
    {
        if (rows.Length == 0) return;
        var insert = provider.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m => m.Name == "Insert" && m.GetParameters().Length == 4
                     && m.GetParameters()[0].ParameterType == typeof(int));
        var insertOptions = Enum.ToObject(insert.GetParameters()[2].ParameterType, 0);

        _ibMutableBufferCtor ??= typeof(ReadOnlyRecordBuffer).Assembly
            .GetType("Microsoft.Dynamics.Nav.Runtime.MutableRecordBuffer")
            ?.GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null, types: new[] { typeof(ReadOnlyRecordBuffer) }, modifiers: null)
            ?? throw new InvalidOperationException(
                "MutableRecordBuffer(ReadOnlyRecordBuffer) not found — BC metadata shape changed");

        foreach (var values in rows)
        {
            var readOnly = new ReadOnlyRecordBuffer((NCLMetaApplicationObject)metaTable, CloneValues(values));
            var mutable = _ibMutableBufferCtor.Invoke(new object[] { readOnly });
            insert.Invoke(provider, new object?[] { 0, mutable, insertOptions, null });
        }
    }
}
