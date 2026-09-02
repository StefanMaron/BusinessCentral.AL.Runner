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

    /// <summary>
    /// BC's TransactionManager.ThrowIfWriteTransactionStarted(), reproduced for the AL
    /// surfaces whose Ncl bodies the runner replaces outright (so BC's own copy of this
    /// check never runs for them).
    ///
    /// BC reaches it from TransactionManager.BeginTransactionWorld, which is what
    /// SessionTransactionExtensions.BeginTransactionWorldAndTransaction calls, which is what
    /// NavCodeunit.DoRunAsync calls on the `errorLevel != DataError.ThrowError` branch — the
    /// branch AL's compiler selects when the Boolean result of `Codeunit.Run` is CONSUMED.
    /// A "transaction world" is an isolated transaction that can be rolled back on its own,
    /// and BC refuses to open one while the caller still has an uncommitted write pending:
    ///
    ///     if (IsTransactionOpenForWrites)
    ///         throw new NavCSideException(PrivacyClassification.SystemMetadata,
    ///                                     Lang.TransactionWorldWithActiveWriteTransactionError)
    ///             { DetailedErrorMessage = Lang.TransactionWorldWithActiveWriteTransaction };
    ///
    /// The statement form (`Codeunit.Run(...)` with the result discarded, DataError.ThrowError)
    /// takes BC's other branch — a plain BeginTransaction that joins the caller's transaction —
    /// and is NOT subject to this check. Whether the return value is consumed is the whole
    /// distinction; see AlRunner#2133 and the corpus's TestCodeunitRunWriteTransaction.al,
    /// which measures all three cases (refused / allowed / allowed-after-Commit) on a real
    /// BC service tier — green on BC 27.5 and 28.3, corpus commit 30d46f95.
    ///
    /// The throw is not trappable by the guarded call's own error trap: in BC's DoRunAsync the
    /// BeginTransactionWorldAndTransaction call sits OUTSIDE the try whose catch suppresses the
    /// codeunit's errors, so this error reaches the AL caller instead of turning into `false`.
    /// Callers must therefore run this check before entering their trap block.
    /// </summary>
    public static void ThrowIfWriteTransactionStarted()
    {
        if (!HasWriteTransaction(null)) return;
        throw BuildTransactionWorldWithActiveWriteTransaction();
    }

    /// <summary>
    /// BC's TransactionManager end of the nested logical transaction a GUARDED
    /// <c>Codeunit.Run</c> opens — the other half of
    /// <see cref="ThrowIfWriteTransactionStarted"/>, which only models the entry guard.
    ///
    /// BC's own NavCodeunit.DoRunAsync, `errorLevel != DataError.ThrowError` branch
    /// (decompiled Ncl.dll):
    ///
    ///     activeSession.BeginTransactionWorldAndTransaction();
    ///     try     { OnRun(record); result = true; }
    ///     catch (NavBaseException) { /* suppressed */ }
    ///     finally { activeSession.EndTransactionWorldAndTransaction(result); }
    ///
    /// TransactionManager.BeginTransaction PUSHES a new LogicalTransaction; EndTransactionImpl
    /// POPS it, committing when `commit` is true. So the run codeunit's writes set
    /// TransactionOpenForWrites on the PUSHED transaction, which is gone by the time the call
    /// returns — the CALLER's transaction is never left open for writes, and the next guarded
    /// Codeunit.Run in the same caller is allowed.
    ///
    /// The runner models the whole stack as one boolean, so without this the first guarded
    /// Codeunit.Run that wrote anything left the flag set and every later guarded run in that
    /// caller was refused with BC's "…the transaction is stopped" error. That is the shape of
    /// RapidStart's Config. Package Management.ApplyPackageRecords apply loop — see
    /// AlRunner#2332.
    ///
    /// A committed nested transaction is exactly as durable, from AL's point of view, as an
    /// explicit Commit() statement (the same reasoning as RecordPatches.NoteTransactionEnd,
    /// AlRunner#1946, which prepends BC's own SessionTransactionExtensions.EndTransaction /
    /// EndTransactionWorldAndTransaction — a hook this path never reaches, because the runner
    /// replaces DoRunAsync outright), so a later unrelated error in the caller must not roll
    /// the run's rows back. ALDatabase_ALCommit is precisely that: clear the write-transaction
    /// flag, and mark a commit point.
    ///
    /// NOT modelled: the <c>commit == false</c> half. BC's EndTransactionWorldAndTransaction(false)
    /// rolls the failed run's own rows back; the runner's snapshot store cannot express a
    /// rollback to the scope's ENTRY state (RecordPatches.NoteTransactionWrite re-baselines on
    /// every write, so RollbackToCommitPoint only restores the state before the LAST write per
    /// table). Rather than half-restore, this leaves the failure path exactly as it behaves
    /// today, tracked as its own gap — see AlRunner#2334.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void EndGuardedRunTransaction(bool commit)
    {
        if (!commit) return;
        ALDatabase_ALCommit();
    }

    /// <summary>
    /// Resolve BC's NavCSideException type.
    ///
    /// It is DEFINED in Microsoft.Dynamics.Nav.Types as
    /// Microsoft.Dynamics.Nav.Types.Exceptions.NavCSideException and TYPE-FORWARDED into
    /// Microsoft.Dynamics.Nav.Ncl as Microsoft.Dynamics.Nav.Runtime.NavCSideException, so
    /// asking Ncl alone for the Runtime name does not reliably resolve it — measured: it
    /// returns null inside the AlRunner.Tests host, which silently degraded every caller
    /// here to a plain InvalidOperationException carrying the right text but the wrong type.
    /// Scan for either spelling across whatever is loaded instead.
    /// </summary>
    private static Type? ResolveNavCSideExceptionType()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType("Microsoft.Dynamics.Nav.Types.Exceptions.NavCSideException", throwOnError: false)
                 ?? asm.GetType("Microsoft.Dynamics.Nav.Runtime.NavCSideException", throwOnError: false);
            if (t != null) return t;
        }
        return null;
    }

    /// <summary>
    /// Read one string out of BC's own resource class.
    ///
    /// The <c>Lang</c> that decompiled Ncl bodies reference is
    /// <c>Microsoft.Dynamics.Nav.Common.Language.Lang</c>, which lives in
    /// <c>Microsoft.Dynamics.Nav.Language.dll</c> — Ncl.dll declares NO type named
    /// <c>Lang</c> at all. Scanning Ncl for one (as this file used to) therefore never
    /// matched, and every caller silently shipped its runner paraphrase instead of BC's
    /// text. Scan the loaded assemblies for the real class instead.
    /// </summary>
    private static string? LangString(string name)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var lang = asm.GetType("Microsoft.Dynamics.Nav.Common.Language.Lang", throwOnError: false);
            var value = lang?.GetProperty(name,
                            System.Reflection.BindingFlags.Static
                            | System.Reflection.BindingFlags.Public
                            | System.Reflection.BindingFlags.NonPublic)
                        ?.GetValue(null) as string;
            if (!string.IsNullOrEmpty(value)) return value;
        }
        return null;
    }

    /// <summary>
    /// Build BC's own NavCSideException carrying Lang.TransactionWorldWithActiveWriteTransactionError
    /// (message) and Lang.TransactionWorldWithActiveWriteTransaction (DetailedErrorMessage), so AL's
    /// asserterror / GetLastErrorText sees the real platform text rather than a runner paraphrase.
    /// Both the Lang resource class and the exception type are resolved by reflection because Lang
    /// lives in Microsoft.Dynamics.Nav.Language.dll, which the runner does not reference directly.
    /// </summary>
    private static Exception BuildTransactionWorldWithActiveWriteTransaction()
    {
        try
        {
            // Fallbacks are BC 28.1's own en-US text (read out of Lang.resources in
            // Microsoft.Dynamics.Nav.Language.dll), used only if the resource cannot be read.
            // Note which way round these go: the AL-VISIBLE message is the deliberately generic
            // "...transaction is stopped" one, and the text that actually names Codeunit.Run is
            // the DetailedErrorMessage, which BC routes to telemetry rather than to AL. AL test
            // code therefore has to match on the generic message.
            var message = LangString("TransactionWorldWithActiveWriteTransactionError")
                ?? "An error occurred and the transaction is stopped. Contact your administrator "
                   + "or partner for further assistance.";
            var detail = LangString("TransactionWorldWithActiveWriteTransaction")
                ?? "The following AL methods are limited during write transactions because one or "
                   + "more tables will be locked: Form.RunModal, Codeunit.Run, Report.RunModal, XmlPort.RunModal.";

            var tCSide = ResolveNavCSideExceptionType();

            const System.Reflection.BindingFlags CtorFlags =
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic;

            // BC constructs this one as NavCSideException(PrivacyClassification.SystemMetadata,
            // message); match that overload when the enum resolves, and fall back to the plain
            // (string) ctor otherwise so the AL-visible message is right either way.
            Exception? ex = null;
            Type? tPrivacy = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                tPrivacy = asm.GetType("Microsoft.Dynamics.Nav.Diagnostic.PrivacyClassification", throwOnError: false);
                if (tPrivacy != null) break;
            }
            if (tCSide != null && tPrivacy != null)
            {
                var ctorPc = tCSide.GetConstructor(CtorFlags, null, new[] { tPrivacy, typeof(string) }, null);
                if (ctorPc != null)
                    ex = (Exception)ctorPc.Invoke(new[] { Enum.ToObject(tPrivacy, 800 /* SystemMetadata */), message });
            }
            if (ex == null)
            {
                var ctor = tCSide?.GetConstructor(CtorFlags, null, new[] { typeof(string) }, null);
                if (ctor == null) return new InvalidOperationException(message);
                ex = (Exception)ctor.Invoke(new object[] { message });
            }

            tCSide!.GetProperty("DetailedErrorMessage",
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(ex, detail);
            return ex;
        }
        catch (Exception ex)
        {
            // Never let the diagnostic construction mask the contract: AL must still see an
            // error here, because BC would have thrown one.
            return new InvalidOperationException(
                "An error occurred and the transaction is stopped. Contact your administrator or "
                + "partner for further assistance. "
                + $"(runner could not build BC's own message: {ex.GetType().Name})");
        }
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
            var typesAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types");
            var tTransactionType = typesAsm?.GetType("Microsoft.Dynamics.Nav.Types.TransactionType");

            var format = LangString("CannotChangeTransactionType");

            object Name(int v) => tTransactionType != null
                ? Enum.ToObject(tTransactionType, v)
                : v;

            var message = format != null
                ? string.Format(System.Globalization.CultureInfo.CurrentCulture, format, Name(current), Name(value))
                : $"You cannot change the transaction type from {Name(current)} to {Name(value)} " +
                  "after the transaction has started.";

            // Same resolution as the write-transaction refusal below: asking Ncl for the
            // type-forwarded ...Runtime spelling alone can come back null, which silently
            // downgraded this to an InvalidOperationException.
            var tCSide = ResolveNavCSideExceptionType();
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

    // ── ALDatabase.ALSid(string) — Windows account name → Windows SID ──────────
    //
    // BC's own body (Ncl 28.1, decompiled) is:
    //
    //     if (string.IsNullOrEmpty(userName))
    //         return NavCurrentThread.Session.User.Sid;
    //     try { return new NTAccount(userName).Translate(typeof(SecurityIdentifier)).ToString(); }
    //     catch (IdentityNotMappedException)          { return string.Empty; }
    //     catch (SystemException ex) when (HResult...) { return string.Empty; }
    //     catch (SystemException ex)                  { throw new NavUserNotFoundException(...); }
    //
    // `NTAccount.Translate` asks the host's Windows identity store (LSA / AD) what SID
    // a name maps to, and .NET documents `IdentityNotMappedException` as the answer when
    // the name maps to nothing. On a Linux host there is no Windows identity store at
    // all, so `System.Security.Principal`'s Unix build throws PlatformNotSupportedException
    // out of the `IdentityReference` constructor before Translate is ever reached. Its
    // HResult (0x80131539) does not match the -2146233087 (0x80131501) the second catch
    // filters on, so BC's last catch converts it into
    //
    //     NavUserNotFoundException: Cannot retrieve the requested user SID. The following
    //     error was reported: Windows Principal functionality is not supported on this platform.
    //
    // which is what AL code sees today. That message describes the runner's host, not an
    // answer to the AL author's question.
    //
    // WHY EMPTY IS THE FAITHFUL ANSWER, NOT A SILENT FAKE.
    // The AL-observable question `Sid(N)` asks is "what SID does this host's Windows
    // identity store map the account name N to?". On the runner's host the answer is
    // provably "none" — for every N, because there is no Windows identity store to map
    // anything. BC already has a documented, AL-visible result for exactly that outcome:
    // the empty string, from its own `catch (IdentityNotMappedException)` branch. So this
    // returns BC's own not-found answer to a question the runner can answer completely.
    //
    // That is a different thing from the anti-pattern loud-failures.md cites — the
    // deleted `ALDatabase_ALSid(string) => "S-1-0-0"` stub. That one FABRICATED an
    // identity: it claimed the named account exists and has a specific SID, which is a
    // statement about a Windows account database the runner has never seen. Reporting
    // absence is not the same as inventing presence, and no AL caller can read an
    // identity out of "".
    //
    // WHAT NO SERVICE TIER HAS ADJUDICATED, AND WHY.
    // A real BC on Windows, joined to a domain in which N *does* exist, returns N's real
    // SID. The runner cannot reproduce that and does not claim to. What the runner claims
    // is only the narrower deployment fact above. This claim could not be settled upstream:
    // the only Linux-capable BC service tier available (StefanMaron/MsDyn365Bc.On.Linux,
    // which is what the al-language corpus CI runs on) replaces this exact method through
    // its StartupHook "Patch #17", returning an FNV-1a hash of the user name shaped like a
    // SID. So the corpus tier answers with a synthetic SID of its own and cannot adjudicate
    // what BC does here. See AlRunner#2312 for the measurement. A BC-on-Windows tier in
    // a domain where the probe account does and does not exist would settle it.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string ALDatabase_ALSidForAccountName(string userName)
    {
        // BC's own first branch: an empty name means "the current session's user", which
        // is session state the runner does populate (an unpopulated windowsSID field →
        // "", already guarded by aldatabase-cluster-1883's Sid_EmptyUserName test).
        if (string.IsNullOrEmpty(userName))
        {
#pragma warning disable CS0618 // NavCurrentThread.Session is [Obsolete] ("expensive"), not unsupported.
            return Microsoft.Dynamics.Nav.Runtime.NavCurrentThread.Session?.User?.Sid ?? string.Empty;
#pragma warning restore CS0618
        }

        // Every other name: not mapped on this host. BC's IdentityNotMapped answer.
        return string.Empty;
    }
}
