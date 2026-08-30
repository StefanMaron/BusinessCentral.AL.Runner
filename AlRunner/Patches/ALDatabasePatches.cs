// ALDatabasePatches — replacements for ALDatabase.AL* getters.
//
// ALDatabase_ALSid / ALDatabase_ALSessionID used to live here (fabricated "S-1-0-0" /
// 42 stubs, wired via a JmpHook registration in BcRuntime.cs) but were deleted in
// #1883's ALDatabase cluster follow-up: JmpHook is disabled by default, so that
// registration was already orphaned, and BC's real, unpatched ALSid(string) /
// ALSessionID() bodies were empirically verified (AL probe against the un-hooked
// build) to run without an NRE — NavCurrentThread.Session is wired to the skeleton.
// The fabricated stub values were exactly the loud-failures.md anti-pattern example
// ("public static string ALDatabase_ALSid(string userName) => "S-1-0-0";" — silent
// fake) — deleting them rather than reviving is the correct direction per that rule
// and per the two prior measurements (#1990-era) that enabling orphaned JmpHooks
// nets negative. See tests/runner-extras/standalone-suites/aldatabase-cluster-1883/.
using System.Runtime.CompilerServices;

namespace AlRunner.Patches;

public static class ALDatabasePatches
{
    // ── Row-version clock ──────────────────────────────────────────────────────
    // BC backs Database.LastUsedRowVersion() / MinimumActiveRowVersion() with SQL's
    // @@DBTS / MIN_ACTIVE_ROWVERSION(). The runner has no SQL connection, so the real
    // bodies NRE inside NavSqlConnectionScope.TryOpenConnection.
    //
    // Faithfulness: the AL-observable contract of @@DBTS is "a positive, strictly
    // monotonic database-wide counter that advances whenever a row is written". We
    // reproduce exactly that with an in-process counter advanced from the same Cecil
    // prepend sites that already stamp system fields on the AL write entry points
    // (ALInsertAsync / ALModifyAsync / ALDeleteAsync / ALRenameAsync). It starts at 1
    // because a BC database always has a non-zero @@DBTS once it has been written to,
    // and AL code reads this value to detect change, never to index storage.
    //
    // NOT faithful for: cross-session/cross-process comparison, and any caller that
    // treats the value as a real SQL rowversion token to hand back to SQL. Both are
    // out of scope for the in-process runner (no SQL to hand it back to).
    private static long _rowVersion = 1;

    // ── Write-transaction state ────────────────────────────────────────────────
    // AL's Database.IsInWriteTransaction() is backed by
    // SessionTransactionExtensions.HasWriteTransaction → DataAccessSource
    // .SessionTransactionManager.AnyHasWriteTransactionStarted(). The runner's in-memory
    // provider never opens one of BC's transactions, so that always answered false.
    //
    // Faithfulness: the AL-observable contract is "a row has been written and not yet
    // committed". BC opens the write transaction on the first write of the AL call and
    // ends it at Commit (or when the invocation unwinds). That is exactly what this flag
    // models — set from the same AL write entry points that move the row-version clock,
    // cleared by Commit and at the per-test isolation boundary.
    //
    // NOT faithful for: rollback semantics (the runner's store has none — see
    // docs/limitations.md) and nested/explicit transaction scopes.
    private static bool _inWriteTransaction;

    /// <summary>Whether an AL write has happened since the last Commit / test boundary.
    /// The session parameter mirrors the signature of the extension method this replaces
    /// (SessionTransactionExtensions.HasWriteTransaction(NavSession)); the runner is
    /// single-session, so there is nothing to distinguish per session.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool HasWriteTransaction(object? session)
        => System.Threading.Volatile.Read(ref _inWriteTransaction);

    /// <summary>Replacement for ALDatabase.ALCommit(). There is nothing to flush — the
    /// in-memory store is written through — but the write transaction ends here, which is
    /// what AL observes via Database.IsInWriteTransaction().</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ALDatabase_ALCommit()
    {
        System.Threading.Volatile.Write(ref _inWriteTransaction, false);
        // Everything written so far is now durable: a later AL error rolls back to HERE,
        // not to the start of the test method.
        RecordPatches.MarkCommitPoint();
    }

    /// <summary>Clear write-transaction state at the per-test isolation boundary, so one
    /// test's uncommitted write cannot make the next test start "in a transaction".</summary>
    public static void ResetWriteTransactionState()
    {
        System.Threading.Volatile.Write(ref _inWriteTransaction, false);
        // The isolation boundary is also a commit point — BC's test framework commits
        // between test methods, which is why a rollback inside one test restores the state
        // the previous test left rather than the state the codeunit started with.
        RecordPatches.MarkCommitPoint();
    }

    // ── Database.CurrentTransactionType ────────────────────────────────────────
    // BC stores this on TransactionManager's current LogicalTransaction. The runner has
    // no TransactionManager, so both the getter and the setter reached skeleton-null
    // state. The default is UpdateNoLocks (0) because that is what a freshly constructed
    // LogicalTransaction carries — BC never assigns the root transaction a type.
    //
    // The setter reproduces BC's own state machine verbatim (TransactionManager
    // .CurrentTransactionType.set): before a transaction has begun, any value is simply
    // stored; once one has begun, a subset of transitions is silently ignored and the
    // rest throw. "A transaction has begun" is the same write-transaction state
    // IsInWriteTransaction() reports, which is precisely what BeginTransactionIssued
    // tracks on BC.
    private static int _currentTransactionType; // TransactionType.UpdateNoLocks

    /// <summary>Replacement for ALDatabase.get_ALCurrentTransactionType.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int ALDatabase_GetCurrentTransactionType()
        => System.Threading.Volatile.Read(ref _currentTransactionType);

    /// <summary>Replacement for ALDatabase.set_ALCurrentTransactionType.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ALDatabase_SetCurrentTransactionType(int value)
    {
        // TransactionType: UpdateNoLocks=0, Update=1, Snapshot=2, Browse=3, Report=4.
        int current = System.Threading.Volatile.Read(ref _currentTransactionType);

        if (!HasWriteTransaction(null))
        {
            // BC: `if (!logicalTransaction.BeginTransactionIssued) { type = value; return; }`
            System.Threading.Volatile.Write(ref _currentTransactionType, value);
            return;
        }

        // BC's switch: `return` means "silently ignored", falling out means "throw".
        bool ignored = current switch
        {
            0 => (uint)(value - 1) > 1u,   // UpdateNoLocks
            1 => true,                     // Update — every change is ignored
            2 => (uint)value > 1u,         // Snapshot
            3 or 4 => (uint)value > 2u,    // Browse / Report
            _ => true,
        };
        if (ignored) return;

        throw BuildCannotChangeTransactionType(current, value);
    }

    /// <summary>
    /// Build BC's own NavCSideException(18023779, Lang.CannotChangeTransactionType) so AL's
    /// asserterror sees the real platform message rather than a runner paraphrase. Resolved
    /// by reflection because Lang is an internal resource-backed class.
    /// </summary>
    private static Exception BuildCannotChangeTransactionType(int current, int value)
    {
        try
        {
            var nclAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
            var typesAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types");
            var tTransactionType = typesAsm?.GetType("Microsoft.Dynamics.Nav.Types.TransactionType");

            string? format = null;
            foreach (var t in nclAsm?.GetTypes() ?? Array.Empty<Type>())
            {
                if (t.Name != "Lang") continue;
                format = t.GetProperty("CannotChangeTransactionType",
                             System.Reflection.BindingFlags.Static
                             | System.Reflection.BindingFlags.Public
                             | System.Reflection.BindingFlags.NonPublic)
                         ?.GetValue(null) as string;
                if (format != null) break;
            }

            object Name(int v) => tTransactionType != null
                ? Enum.ToObject(tTransactionType, v)
                : v;

            var message = format != null
                ? string.Format(System.Globalization.CultureInfo.CurrentCulture, format, Name(current), Name(value))
                : $"You cannot change the transaction type from {Name(current)} to {Name(value)} " +
                  "after the transaction has started.";

            var tCSide = nclAsm?.GetType("Microsoft.Dynamics.Nav.Runtime.NavCSideException");
            var ctor = tCSide?.GetConstructor(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic,
                null, new[] { typeof(int), typeof(string) }, null);
            if (ctor != null)
                return (Exception)ctor.Invoke(new object[] { 18023779, message });

            return new InvalidOperationException(message);
        }
        catch (Exception ex)
        {
            // Never let the diagnostic construction mask the real contract: AL must still
            // see an error here, because BC would have thrown one.
            return new InvalidOperationException(
                "You cannot change the transaction type after the transaction has started. " +
                $"(runner could not build BC's own message: {ex.GetType().Name})");
        }
    }

    /// <summary>Record an AL-visible row write. Called from the AL write entry points via
    /// Cecil prepend (Modify/Delete/Rename/DeleteAll/ModifyAll — see
    /// <see cref="NoteRecordInsertWrite"/> for Insert's own, deliberately extended, prepend),
    /// so every write moves the row-version counter exactly as a SQL write moves @@DBTS, and
    /// opens the write transaction exactly as BC's first write does.
    ///
    /// Temporary records are excluded from both: a temp-table write touches no database,
    /// so it neither advances @@DBTS nor starts a write transaction on real BC.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NoteRecordWrite(object? record)
    {
        if (record is Microsoft.Dynamics.Nav.Runtime.NavRecord { IsTemporary: true }) return;
        System.Threading.Interlocked.Increment(ref _rowVersion);
        System.Threading.Volatile.Write(ref _inWriteTransaction, true);
        // Take the rollback snapshot now, before this write lands — refreshed on EVERY
        // write, not just the first since the last commit point (AlRunner#2142; see
        // RecordPatches.NoteTransactionWrite's doc for why the refresh can't be skipped).
        RecordPatches.NoteTransactionWrite(record);
    }

    /// <summary>Record an AL-visible Insert. Prepended to NavRecord.ALInsertAsync only
    /// (AlRunner#2142) — identical to <see cref="NoteRecordWrite"/> (same rowversion /
    /// write-transaction bookkeeping, same rollback-snapshot refresh, so an uncommitted
    /// Insert() still rolls back normally when a LATER, unrelated statement fails — see
    /// TestAssertErrorRollback.al), PLUS a note of the attempt for
    /// <see cref="RecordPatches.ForceDurableFailedInserts"/>: measured real BC keeps an
    /// inserted row durable even when THAT SAME Insert() statement's own OnInsert trigger
    /// throws. Decompiling NavRecord.InsertAsync shows OnInsert runs BEFORE the only call
    /// that physically writes anything, with no surrounding try/catch, identically in this
    /// runner and (presumably) real BC — so the row genuinely is never written when OnInsert
    /// throws, in either. The force-durable step reproduces real BC's OBSERVED outcome
    /// without claiming to model how real BC reaches it; see the long comment atop
    /// RecordPatches.TransactionSnapshot.cs and AlRunner#2167.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NoteRecordInsertWrite(object? record)
    {
        if (record is Microsoft.Dynamics.Nav.Runtime.NavRecord { IsTemporary: true }) return;
        System.Threading.Interlocked.Increment(ref _rowVersion);
        System.Threading.Volatile.Write(ref _inWriteTransaction, true);
        RecordPatches.NoteTransactionWrite(record);
        RecordPatches.NoteInsertAttempt(record);
    }

    /// <summary>Replacement for ALDatabase.ALLastUsedRowVersion() — the runner's
    /// @@DBTS equivalent. Positive and non-decreasing; advances on every row write.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Microsoft.Dynamics.Nav.Runtime.NavBigInteger ALDatabase_ALLastUsedRowVersion()
        => Microsoft.Dynamics.Nav.Runtime.NavBigInteger.Create(
            System.Threading.Interlocked.Read(ref _rowVersion));

    /// <summary>Replacement for ALDatabase.ALMinimumActiveRowVersion().
    /// SQL's MIN_ACTIVE_ROWVERSION() returns the lowest row version among open
    /// transactions, or @@DBTS + 1 when none are open. The runner executes AL on a
    /// single session with no concurrent open transactions, so the second branch is
    /// the correct one — always @@DBTS + 1, never below LastUsedRowVersion.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Microsoft.Dynamics.Nav.Runtime.NavBigInteger ALDatabase_ALMinimumActiveRowVersion()
        => Microsoft.Dynamics.Nav.Runtime.NavBigInteger.Create(
            System.Threading.Interlocked.Read(ref _rowVersion) + 1);

    /// <summary>Replacement for ALDatabase.ALTenantID().
    /// Returns a fixed non-empty tenant id stub. The real getter reaches into
    /// NavCurrentThread.Session.Tenant.Id which does not exist on the skeleton
    /// thread. Value 'STANDALONE' matches BC's standalone-mode convention used
    /// by 318-navtext-string-rewrite.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string ALDatabase_ALTenantID() => "STANDALONE";
}
