// ALDatabasePatches — replacements for ALDatabase.AL* getters.
//
// Rationale: BC's real ALDatabase.ALSid (and siblings like ALCompanyName,
// ALSerialNumber, ALTenantID, ALUserSecurityID) reach into service-tier
// session state that does not exist in the skeleton runtime. The real body
// NREs on `NavCurrentThread.Session.Identity` chain access.
//
// JmpHook on the static `ALDatabase.ALSid(string)` entry point fires
// reliably (confirmed 2026-05-18 R2R spike — `feedback_r2r_envvar_doesnt_help`).
// So the same constant-returning replacement strategy used elsewhere works
// here: return a BC-faithful stub SID (`S-1-0-0` — the well-known NULL_SID
// constant; clearly a placeholder, not a real domain SID).
//
// Faithfulness boundary: callers that only check non-empty / consistent /
// not-equal-to-real-SID-prefix observe the same behaviour as a real session.
// Callers that parse / validate / compare against a real account SID will
// see stub values and should not be in scope (see `docs/scope.md §3.8 auth`).
using System.Runtime.CompilerServices;

namespace AlRunner.Patches;

public static class ALDatabasePatches
{
    /// <summary>Constant stub SID — NULL_SID per Microsoft's well-known SID list.
    /// Clearly a placeholder; not equal to any real domain SID prefix
    /// (e.g. 'S-1-5-21') so the negative SID_NotRealWindowsSid test still passes.</summary>
    private const string StubSid = "S-1-0-0";

    /// <summary>Replacement for ALDatabase.ALSid(string userName).
    /// Returns a fixed, non-empty, non-real-SID stub.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string ALDatabase_ALSid(string userName) => StubSid;

    /// <summary>Replacement for ALDatabase.ALSessionID().
    /// Returns a fixed positive integer stub (42). The real getter reaches into
    /// NavCurrentThread.Session which does not exist in the skeleton runtime.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int ALDatabase_ALSessionID() => 42;

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

    /// <summary>Advance the row-version clock. Called from the AL write entry points
    /// via Cecil prepend, so every AL-visible row write moves the counter exactly as a
    /// SQL write moves @@DBTS.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void BumpRowVersion() => System.Threading.Interlocked.Increment(ref _rowVersion);

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
