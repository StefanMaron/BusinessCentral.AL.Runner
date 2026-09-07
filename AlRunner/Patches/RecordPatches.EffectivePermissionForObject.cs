// RecordPatches.EffectivePermissionForObject — answer "what may this user do to this object"
// from the runner's own stated permission position, instead of dereferencing a null cache.
//
// THE DEFECT (AlRunner#2382)
//   `TestPage "Company Information".OpenEdit()` never opened the page at all:
//
//     NavNCLDotNetInvokeException: A call to
//     Microsoft.Dynamics.Nav.NavUserAccount.NavUserAccountHelper.GetEffectivePermissionForObject
//     failed with this message: Object reference not set to an instance of an object.
//       "Effective Permissions Mgt."(CodeUnit 9852).PopulatePermissionRecordWithEffectivePermissionsForObject
//       "Monitor Sensitive Field"(CodeUnit 1392).CheckPermission
//       "Monitor Sensitive Field"(CodeUnit 1392).ValidateUserPermissions
//       "Monitor Sensitive Field"(CodeUnit 1392).ShowPromoteMonitorSensitiveFieldNotification
//       "Company Information"(Page 1).OnOpenPage
//
//   #2382 was filed against the CLOSE of that page (its OnClosePage experience-tier refusal
//   never raising). The close was never the reachable part: OnOpenPage throws, so the page
//   variable is never usable and the page's own `Experience` global — which OnClosePage reads —
//   is never assigned. The issue asks which of two things is broken, "whether OnOpenPage
//   populated Experience at all, or whether Close() reaches OnClosePage"; measured, it is the
//   first, and the second was already correct (RunnerPageInstance.RaiseOnClosePage).
//
// THE MECHANISM — THE SAME NULL AS #3039, ONE METHOD OVER
//   `NavUserAccountHelper.GetEffectivePermissionForObject` forwards into Ncl, whose body is
//   (decompiled from Microsoft.Dynamics.Nav.Ncl.dll, BC 28.1.49838.53910):
//
//     public static async ValueTask<PermissionMask> GetEffectivePermissionForObjectAsync(
//         NavSession session, Guid userSecurityId, string companyName, ApplicationObjectId objectId)
//     {
//         bool isCurrentUser = session.User.Id == userSecurityId;
//         NavUser user = isCurrentUser ? session.User
//                                      : await GetUserWithEntitlementAsync(session, userSecurityId);
//         return (isCurrentUser ? session.Permissions : new NavUserPermissions(user, session))
//                .GetEffectivePermissionForObject(companyName, objectId);
//     }
//
//   `session.Permissions` is null on the skeleton session — a private setter only the real
//   session bring-up calls. RecordPatches.PermissionSetAssignment.cs's header records the same
//   null from `IsPermissionSetAssignedAsync`, two methods away in the same class, and so do
//   EventSubscriberPatches and BcRuntime from their own encounters with it.
//
// WHY THE STATE CANNOT SIMPLY BE POPULATED
//   The same wall as #3039, and it is the INNER call that hits it:
//
//     internal PermissionMask GetEffectivePermissionForObject(string companyName, ApplicationObjectId objectId)
//     {
//         PermissionManager pm = GetPermissionManager(tenant.Database.CompanyTokens.Get(companyName));
//         return pm.IsPermissionSystemEnabled ? pm.GetPermissions(objectId)
//                                             : pm.GetLicensePermissions(objectId);
//     }
//
//   `tenant.Database` is the SQL-backed NavDatabase that BcRuntime documents as out of scope
//   after measuring the full bring-up chain break ~466 tests. So handing BC a real
//   NavUserPermissions moves the NRE rather than removing it.
//
// WHAT IT ANSWERS, AND WHY THAT IS NOT A NEW CLAIM
//   `PermissionMask.MaxDirect` (0x1F = Read|Insert|Modify|Delete|Execute) for the session's own
//   user, which is the mask a SUPER user holds on every object.
//
//   That the skeleton session runs as SUPER is not decided here. It is the position this runner
//   already ships, and every one of these would have to be reversed with it:
//
//     * NclCecilRewrite.Runtime.cs rewrites RecordImplementation.VerifyPermissions -> a NO-OP,
//       i.e. no read/insert/modify/delete check ever refuses anything.
//     * NclCecilRewrite.Metadata.cs rewrites NavSession.HasExecutePermission /
//       HasCachedExecutePermissions / HasExecutePermissionForCompany /
//       HasExecutePermissionForAllCompanies -> `true`, and no-ops VerifyExecutePermission.
//     * NclCecilRewrite.Runtime.cs no-ops NavSession.MaximizePermissions /
//       RemoveMaximizedPermissions ("equivalent to SUPER everywhere").
//     * RecordPatches.PermissionSetAssignment.cs answers IsSuper(UserSecurityId()) true, and
//       RecordPatches.AccessControlSeed.cs seeds the Access Control row that says so — pinned
//       upstream by corpus codeunit 60889, green on all eight required BC legs.
//
//   Answering anything less than MaxDirect here would make the runner contradict itself: a
//   record write that VerifyPermissions waves through would be reported by
//   GetEffectivePermissionForObject as not permitted. MaxDirect is the mask that agrees with
//   the rest of the runner, and it is what a real BC test tier's SUPER user measures too.
//
//   Indirect* bits are deliberately NOT set. They are strictly weaker than their direct
//   counterparts — "may do this only through another object" — so a user already holding the
//   direct bit gains nothing from them, and BC's own MaxDirect constant is exactly this set.
//
// THE ONE THING IT DOES NOT CLAIM (loud-failures.md)
//   A user OTHER than the session's own. The runner holds no per-user permission state that
//   could distinguish them, and answering MaxDirect for an arbitrary user id would be the
//   silent fake .claude/rules/loud-failures.md forbids — AL asking "what may THAT user do"
//   would be told "everything", with nothing behind it. That arm throws
//   RunnerOutOfScopeException naming the API and the reason. It is genuinely unreached by the
//   AL that motivated this: codeunit 1392 asks about `UserSecurityId()`.
//
// PRECOMPILED-DLL RESPECT
//   `PermissionManagement` is in Ncl.dll — the runtime engine, ours to rewrite per
//   .claude/rules/precompiled-dll-respect.md. No AL business-logic body is touched: codeunits
//   1392 and 9852, and page 1's own OnOpenPage, run their own real bodies and get an answer
//   instead of an NRE. The rewrite is registered in NclCecilRewrite.Runtime.cs's cluster batch
//   alongside its #3039 sibling and imports only this helper's memberRef.
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// Cecil-owned replacement for
    /// <c>Microsoft.Dynamics.Nav.Runtime.PermissionManagement.GetEffectivePermissionForObjectAsync</c>.
    /// See this file's header for the mechanism, for why the answer is computed rather than
    /// delegated back to BC, and for why the "some other user" arm refuses instead.
    ///
    /// Synchronous by construction — nothing it reads leaves memory — so it hands back an
    /// already-completed <see cref="ValueTask{T}"/> rather than introducing a suspension point
    /// into a call chain the runner drives from one AL thread. Same shape as its #3039 sibling.
    /// </summary>
    public static ValueTask<PermissionMask> PermissionManagement_GetEffectivePermissionForObjectAsync(
        NavSession session, Guid userSecurityId, string companyName, ApplicationObjectId objectId)
        => new(EffectivePermissionForObjectCore(session, userSecurityId, objectId));

    private static PermissionMask EffectivePermissionForObjectCore(
        NavSession session, Guid userSecurityId, ApplicationObjectId objectId)
    {
        if (!IsSkeletonSessionUser(session, userSecurityId))
            throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                // No " — " in the api argument; see RequireRecord.
                "NavUserAccountHelper.GetEffectivePermissionForObject (other user)",
                "effective-permissions-other-user — the runner holds no per-user permission "
                + $"state, so what user {userSecurityId} may do to object {objectId} cannot be "
                + "answered; only the session's own user can. See docs/scope.md");

        // The runner's session is SUPER, so its direct mask on every object is BC's own
        // MaxDirect (Read|Insert|Modify|Delete|Execute). Stated once, here; see the header for
        // the four sibling rewrites that already depend on it.
        return PermissionMask.MaxDirect;
    }
}
