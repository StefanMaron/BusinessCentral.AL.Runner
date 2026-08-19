// RecordPatches.TransactionSnapshot — AL's write-transaction rollback.
//
// WHAT AL PROMISES
//   An AL error rolls the database back to the last COMMIT. The test framework establishes
//   a commit point at the start of every test method, and AL's Commit() establishes another.
//   BC implements the rollback half in its own code: NavMethodScope.AssertError catches the
//   error and calls session.Rollback().
//
//   That is observable, and the corpus pins it:
//     * TestTriggerRollback.OnInsert_Throws_RecordNotInserted — Initialize() deletes every
//       row, the Insert then fails in OnInsert, and the table is left holding the row the
//       PREVIOUS test method committed: the DeleteAll was rolled back too.
//     * OnModify_Throws_ValueNotModified — an explicit Commit() before the asserterror,
//       precisely so the Insert above it survives the rollback.
//     * OnDelete_Throws_RecordStillExists — no Commit(), so the rollback restores the row
//       the previous test method left, and Get() finds it.
//
//   Without this the runner never rolled anything back, so an asserterror left every write
//   made before it in place. Green either way for a test that only checks the error text —
//   and silently wrong for one that checks what is in the table afterwards.
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
//   The snapshot is taken LAZILY, on the first write since the last commit point (see
//   ALDatabasePatches.NoteRecordWrite, which is prepended to every NavRecord AL write
//   entry). A test that only reads pays nothing.
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

    /// <summary>
    /// Establish a commit point: everything written up to now survives a later rollback.
    /// Called at each test-method boundary and from AL's <c>Commit()</c>.
    /// </summary>
    public static void MarkCommitPoint() => _txCommitPoint.Clear();

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
    /// Snapshot the record's table if this is its first write since the last commit point.
    /// Called from <see cref="ALDatabasePatches.NoteRecordWrite"/>, which BC's own AL write
    /// entry points run before doing anything.
    ///
    /// Per table, and lazily: a test that never writes pays nothing, and one that writes a
    /// single table does not copy the whole install-seeded store.
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
            if (_txCommitPoint.ContainsKey(key)) continue;

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
