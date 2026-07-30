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

    /// <summary>Replacement for ALDatabase.ALTenantID().
    /// Returns a fixed non-empty tenant id stub. The real getter reaches into
    /// NavCurrentThread.Session.Tenant.Id which does not exist on the skeleton
    /// thread. Value 'STANDALONE' matches BC's standalone-mode convention used
    /// by 318-navtext-string-rewrite.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string ALDatabase_ALTenantID() => "STANDALONE";
}
