// RecordPatches.TransactionSnapshot — AL's write-transaction rollback.
//
// WHAT AL PROMISES
//   An AL error rolls the database back to the last COMMIT. The test framework establishes
//   a commit point at the start of every test method, and AL's Commit() establishes another.
//   BC implements the rollback half in its own code: NavMethodScope.AssertError catches the
//   error and calls session.Rollback().
//
//   That is observable, and the corpus pins it:
//     * TestTriggerRollback.OnModify_Throws_ValueNotModified — an explicit Commit() before
//       the asserterror, precisely so the Insert above it survives the rollback.
//     * TestTriggerRollback.OnDelete_Throws_RecordStillExists — Insert() (no error, no
//       Commit()) then `asserterror Delete()` whose OnDelete trigger throws: the Insert must
//       survive. NOT because a trigger-failing write is exempt from rollback — the test
//       IMMEDIATELY BEFORE this one in the same codeunit (OnModify_Throws_ValueNotModified)
//       does an Insert() + Commit(), so the true commit-point baseline for this table already
//       includes that row. Rolling back to it (undoing this test's own uncommitted Insert)
//       lands on exactly the same live row — the survival is a coincidence of which value the
//       baseline holds, not a special case. AlRunner#2431 confirmed this by cross-referencing
//       the two tests' declaration order and by finding the same shape holds for
//       OnInsert_Throws_RecordNotInserted below.
//     * TestTriggerRollback.OnInsert_Throws_RecordNotInserted — asserterror wraps the Insert
//       call itself, OnInsert's own trigger throws, and the row is never physically written
//       (decompiled NavRecord.InsertAsync runs OnInsert BEFORE the only call that physically
//       inserts anything, with no surrounding try/catch — identically in this runner, which
//       reuses that method unmodified, and in real BC). Its Count()=1 assertion does NOT come
//       from a phantom row conjured out of the failed insert (AlRunner#2142's ForceDurable
//       FailedInserts, removed by #2431) — it comes from the SAME mechanism as
//       OnDelete_Throws_RecordStillExists above: the immediately preceding test
//       (OnInsert_NoError_InsertSucceeds) leaves a row committed only by virtue of NOT hitting
//       an asserterror at all (nothing ever rolled it back), and THIS test's own Initialize()
//       does an uncommitted DeleteAll(false) that the general rollback below undoes. See
//       AlRunner#2431 for the isolated repro (F1/F1b/F1c arms) that told the two apart:
//       Insert(true) whose OnInsert throws, with NOTHING else going on, leaves the table
//       genuinely empty on real BC (F1 — no phantom row, ever) and the key is immediately
//       free again for a plain re-Insert (F1b).
//     * AlRunner#2142 originally added the phantom-row model (ForceDurableFailedInserts) plus
//       a per-table "did the last write land" scope tracker (SettleAssertErrorScopeWrites,
//       #2191) to compensate for it. Both are gone as of #2431 — the plain, unconditional
//       "roll back to the true commit-point baseline" rule below already produces the right
//       answer for every one of TestTriggerRollback.al's 8 tests, TestAssertErrorRollback.al's
//       6, and the #2431 repro's 7 arms; no per-table pruning or scope-relative exemption is
//       needed at all. #2167's "what mechanism does real BC use" question is retired along
//       with the model it was trying to explain.
//
//   AlRunner#2142 also originally cited TestScopeIsolationContracts.Test04 and
//   TestTransactionContracts.Error_After_Insert_Before_Commit_RecordPersists as examples of
//   the same bug — both assert the OPPOSITE of TestAssertErrorRollback.al (Codeunit 60943)
//   Record_Insert_UnrelatedAssertError_NoCommit_RowIsRolledBack for what looks like the
//   identical shape (an uncommitted, untriggered Insert() then a later, unrelated Error()).
//   Real BC passes all three (confirmed against CI run 33273501078, BC 27.5 and 28.3) — they
//   are NOT contradictory. This runner currently does NOT special-case that shape at all —
//   Test04 and Error_After_Insert_Before_Commit_RecordPersists both fail here, openly, with no
//   known-gaps entry masking it, exactly as on unmodified main.
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
//   The snapshot is taken ONCE per table, lazily, on the FIRST write to that table since the
//   last commit point (MarkCommitPoint, called at each test-method boundary and from AL's
//   Commit()) — never refreshed after that, no matter how many separate statements write to
//   the table or whether any of those writes' own triggers subsequently throw. On an
//   asserterror catch, RollbackToCommitPoint restores every snapshotted table to exactly that
//   baseline, unconditionally. This one rule is what TestAssertErrorRollback.al,
//   TestTriggerRollback.al, and the AlRunner#2431 repro all turn out to need — see the WHAT AL
//   PROMISES section above for how the two tests that look like they need special-casing
//   (OnDelete_Throws_RecordStillExists, OnInsert_Throws_RecordNotInserted) actually don't.
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
    /// Capture the pre-write image of the record's table, ONCE per table, lazily, on the
    /// FIRST write since the last commit point — never refreshed after that (AlRunner#2431
    /// removed the per-scope refresh #2191 added; see the file header). This is what ANY
    /// later asserterror rolls back to, unconditionally, however many separate writes landed
    /// and however many statements — inside or outside the asserterror'd statement itself —
    /// they were spread across.
    ///
    /// Called from <see cref="ALDatabasePatches.NoteRecordWrite"/> /
    /// <see cref="ALDatabasePatches.NoteRecordInsertWrite"/>, which BC's own AL write entry
    /// points run before doing anything — so the capture is always the state BEFORE this
    /// particular write, whether or not it goes on to succeed.
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
