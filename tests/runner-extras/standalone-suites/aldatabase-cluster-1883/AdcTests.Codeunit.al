/// #1883 follow-up to #1800/#1947/#1970/#1990/#2004/#2014 — one more cluster of orphaned
/// `Hook(...)` (JmpHook) registrations in AlRunner/BcRuntime.cs, measured via
/// `AL_RUNNER_HOOK_AUDIT=1` against the al-language corpus. JmpHook is disabled by default
/// (see AlRunner/Infrastructure/JmpHook.cs), so this cluster was a silent no-op — BC's real,
/// unpatched body ran instead, and the corpus (2130/2130) was already green before this PR
/// touched anything, and stays byte-identical after.
///
/// Cluster — 9 of ALDatabase's 15 orphaned registrations (#1883's fresh breakdown listed
/// "ALDatabase — 15"). Each was checked by decompiling Microsoft.Dynamics.Nav.Ncl.dll
/// (ilspycmd) AND by running an AL probe against the un-hooked build — not by trusting the
/// deleted registrations' own comments, which #2004/#2014 already showed can be stale:
///
///   - ALSid(string) / ALSessionID() — the deleted comments claimed both "reach into
///     NavCurrentThread.Session, which does not exist in the skeleton runtime". Stale:
///     Session IS wired to the skeleton (confirmed by the pre-existing ALUserID/
///     ALUserSecurityId/ALTenantID field-poke seeding in BcRuntime.cs). ALSid("") reads
///     session.User.Sid (backed by an unpopulated `windowsSID` field → "", not a crash).
///     ALSessionID() reads session.Id after session.CheckConnectionIsOpen() (hasBeenOpened is
///     seeded true elsewhere) — session.Id is an auto-property with `= -1` field initializer,
///     but the skeleton session is built via RuntimeHelpers.GetUninitializedObject, which skips
///     field initializers, so the CLR default 0 survives instead. 0 satisfies the corpus's own
///     TestFinalCoverage.al SessionId_WithDatabasePrefix_ReturnsNonNegative /
///     SessionId_IsCallable assertions (`>= 0`) — MORE faithful than the deleted hook's
///     fabricated 42. Deleted outright, along with the now-unreferenced
///     ALDatabasePatches.ALDatabase_ALSid / _ALSessionID stubs (the literal "S-1-0-0" silent
///     fake loud-failures.md itself cites as the anti-pattern to avoid reviving).
///
///   - get_ALSerialNumber — the ONE registration in this cluster that genuinely misbehaves
///     standalone: NavSession.get_License() throws NullReferenceException before
///     ALSerialNumber's own body runs (confirmed both by decompile and by the probe below
///     catching the real exception type/stack). Per this issue's rule 2 (genuine bug → Cecil
///     redirect, not deletion), this is now Cecil-owned in NclCecilRewrite.cs instead of
///     JmpHook-owned — same "STANDALONE" sentinel the deleted hook used to return, now applied
///     unconditionally regardless of the JmpHook toggle.
///
///   - get_/set_ALLockTimeout, get_/set_ALLockTimeoutDuration — the deleted comment claimed
///     these "NRE on session.Database" via DataAccessSource.CreateTenantDataProvider(). Stale:
///     Database.LockTimeout(true) followed by Database.LockTimeout() round-trips correctly end
///     to end on the un-hooked build (returns true) — MORE faithful than the deleted hook would
///     have been (its get_ALLockTimeout stub ALWAYS returned false regardless of what was set,
///     breaking exactly the round-trip the real body gets right).
///
///   - ALGetDefaultTableConnection — reads
///     NavCurrentThread.Session.TableConnectionManager.GetDefaultTableConnection(...), which
///     returns "" (no connections registered) without an NRE. Already exercised today by the
///     corpus (TestSystemExtended.al
///     DatabaseGetDefaultTableConnection_ExternalSQL_CloudSandbox_IsCallable, which only
///     asserts callability) — unaffected by deleting the dead hook.
///
///   - ALImportData — reaches session.ClientCallback.ImportDataAction(...), which raises BC's
///     real NavNCLCallbackNotAllowedException ("Callback functions are not allowed.") when no
///     test handler is registered — the same "unhandled interactive callback" behaviour AL
///     tests already rely on for Message/Confirm dispatch elsewhere in the runner, not a
///     runner crash. MORE faithful than the deleted hook's silent `false` (real BC never
///     silently returns false here — it raises this exact exception). No corpus test exercises
///     ImportData today.
///
/// NOT resolved by this cluster (left as still-orphaned, deferred — see BcRuntime.cs comments
/// at each site for why): ALChangeUserPassword(2/3), ALSetUserPassword(2/3),
/// ALDataFileInformation (all touch the real virtual User/User Password tables + crypto
/// services with zero corpus coverage to lean on) and ALAlterKeyAsync (its validation-guard
/// path works standalone for free, but the real alterable-key path genuinely NREs several
/// frames into SQL DDL string-building — needs a narrower fix than a blanket redirect).
///
/// The tests below are regression guards with a narrower runner-mechanism claim than full BC
/// behaviour: "no redirect fires here, and BC's real, unpatched ALDatabase body produces this
/// exact, reproducible result" — not a re-proof of Database.SessionId/Sid/LockTimeout's BC
/// contract itself (SessionId already has upstream corpus coverage; the others have none and
/// are runner-mechanism claims by construction, same framing as
/// nav-cancellation-token-1883/NctTests.Codeunit.al and the other #1883-follow-up suites in
/// this consolidated app — see bc-behavior-tests-go-upstream.md).
codeunit 60708 "ADC Tests"
{
    Subtype = Test;

    // No Codeunit Assert dependency in this app (see the sibling suites in this consolidated
    // app.json) — plain Error() with an explicit condition check plays the same role.

    // ── ALDatabase.ALSid(string) — deleted hook, real body returns "" (no crash) ─────────────
    [Test]
    procedure Sid_EmptyUserName_ReturnsEmptyString_NoThrow()
    var
        Sid: Text;
    begin
        Sid := Database.Sid('');
        if Sid <> '' then
            Error('Expected Database.Sid('''') to return the unpopulated-windowsSID empty '
                + 'string on the skeleton runtime, got: %1', Sid);
    end;

    // ── ALDatabase.ALSessionID() — deleted hook, real body returns 0 (no crash) ──────────────
    [Test]
    procedure SessionId_ReturnsZero_NonNegative()
    var
        Id: Integer;
    begin
        Id := Database.SessionId();
        if Id <> 0 then
            Error('Expected Database.SessionId() to return the uninitialized-object default 0 '
                + 'on the skeleton runtime, got: %1', Id);
        if Id < 0 then
            Error('Database.SessionId() must be non-negative, got: %1', Id);
    end;

    [Test]
    procedure SessionIdGlobal_ReturnsZero_NonNegative()
    var
        Id: Integer;
    begin
        Id := SessionId();
        if Id <> 0 then
            Error('Expected the global SessionId() to return 0 on the skeleton runtime, got: %1', Id);
    end;

    // ── ALDatabase.get_ALSerialNumber — genuinely NREs; now Cecil-owned ──────────────────────
    [Test]
    procedure SerialNumber_ReturnsStandaloneSentinel()
    var
        Serial: Text;
    begin
        Serial := Database.SerialNumber();
        if Serial <> 'STANDALONE' then
            Error('Expected Database.SerialNumber() to return the Cecil-owned STANDALONE '
                + 'sentinel (same one Database.TenantId() returns), got: %1', Serial);
    end;

    // ── ALDatabase.{get,set}_ALLockTimeout — deleted hook, real body round-trips ─────────────
    [Test]
    procedure LockTimeout_SetTrue_ReadsBackTrue()
    begin
        Database.LockTimeout(true);
        if not Database.LockTimeout() then
            Error('Expected Database.LockTimeout() to read back TRUE after '
                + 'Database.LockTimeout(true) via the real un-hooked body');
    end;

    [Test]
    procedure LockTimeout_SetTrueThenFalse_ReadsBackFalse()
    begin
        Database.LockTimeout(true);
        Database.LockTimeout(false);
        if Database.LockTimeout() then
            Error('Expected Database.LockTimeout() to read back FALSE after '
                + 'Database.LockTimeout(false) via the real un-hooked body');
    end;

    // ── ALDatabase.ALGetDefaultTableConnection — deleted hook, real body returns "" ──────────
    [Test]
    procedure GetDefaultTableConnection_ExternalSql_ReturnsEmptyString_NoThrow()
    var
        ConnName: Text;
    begin
        ConnName := Database.GetDefaultTableConnection(TableConnectionType::ExternalSQL);
        if ConnName <> '' then
            Error('Expected Database.GetDefaultTableConnection(ExternalSQL) to return "" '
                + '(no connections registered on the skeleton), got: %1', ConnName);
    end;

    // ── ALDatabase.ALImportData — deleted hook, real body raises BC's own callback-not- ──────
    // allowed exception rather than silently returning false.
    [Test]
    procedure ImportData_NoTestHandler_RaisesCallbackNotAllowed()
    var
        FileName: Text;
    begin
        asserterror Database.ImportData(false, FileName);
        Assert_ExpectedError('Callback functions are not allowed.');
    end;

    /// <summary>Minimal GetLastErrorText-based assert, matching the plain-Error() style this
    /// suite uses elsewhere (no Codeunit Assert dependency).</summary>
    local procedure Assert_ExpectedError(ExpectedSubstring: Text)
    var
        ActualError: Text;
    begin
        ActualError := GetLastErrorText();
        if not ActualError.Contains(ExpectedSubstring) then
            Error('Expected the error to contain ''%1'', got: %2', ExpectedSubstring, ActualError);
    end;
}
