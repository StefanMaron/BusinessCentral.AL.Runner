// RecordPatches.PermissionSetAssignment — answer "is this permission set assigned to this
// user" from state the runner actually has, instead of dereferencing a null permission cache.
//
// THE DEFECT (AlRunner#3039)
//   `User.Modify(true)` on a well-formed User row raised, from inside a Base Application
//   subscriber, an NRE that surfaced as:
//
//     NavNCLDotNetInvokeException: A call to
//     Microsoft.Dynamics.Nav.NavUserAccount.NavUserAccountHelper.IsPermissionSetAssigned
//     failed with this message: Object reference not set to an instance of an object.
//       "User Permissions Impl."(CodeUnit 153).HasUserPermissionSetAssigned
//       "User Permissions Impl."(CodeUnit 153).IsSuper
//       "User Permissions Impl."(CodeUnit 153).CanManageUsersOnTenant
//       "User Permissions"(CodeUnit 152).CanManageUsersOnTenant
//       "Permission Manager"(CodeUnit 9002).CheckCurrentUserCanModifyUser
//       "User"(Table 2000000120).InjectedEventMethodScope
//
//   It only became reachable once #2932 (PR #2979) stopped BC's `memberId == 0` dispatch
//   branch from discarding a precompiled subscriber's `ValueTask`. Before that the subscriber
//   was abandoned at its first suspension and never reached `IsSuper` at all.
//
// THE MECHANISM, MEASURED
//   `NavUserAccountHelper.IsPermissionSetAssigned` is a two-line forwarder (decompiled from
//   Microsoft.Dynamics.Nav.NavUserAccount.dll, BC 28.1.49838.53910):
//
//     public static bool IsPermissionSetAssigned(Guid userSecurityId, string companyName,
//                                                string roleId, Guid appId, int permissionScope)
//         => PermissionManagement.IsPermissionSetAssignedAsync(
//                Session, userSecurityId, new PermissionSetKey(roleId, appId,
//                (PermissionScope)permissionScope), companyName).AsTask().GetAwaiter().GetResult();
//
//   and the Ncl method it forwards to is:
//
//     public static async ValueTask<bool> IsPermissionSetAssignedAsync(
//         NavSession session, Guid userSecurityId, PermissionSetKey permissionSet, string companyName)
//     {
//         bool isCurrentUser = session.User.Id == userSecurityId;
//         NavUser navUser = isCurrentUser ? session.User
//                                         : await GetUserWithEntitlementAsync(session, userSecurityId);
//         return (isCurrentUser ? session.Permissions
//                               : new NavUserPermissions(navUser, session))
//                .HasRole(companyName, permissionSet);
//     }
//
//   The null is `session.Permissions`. `NavSession.User` is NOT null on the skeleton — it is
//   `Authenticator.User`, and BcRuntime field-pokes a populated Authenticator — but
//   `NavSession.Permissions` has a private setter that only the real session bring-up calls,
//   so it stays null. Two other files in this repo already record the same fact from their own
//   encounters with it (EventSubscriberPatches's ReplaceAttributeWithZeroedCopy, and
//   BcRuntime's NavRecord.TestFieldNotBlank note).
//
// WHY THE STATE CANNOT SIMPLY BE POPULATED
//   The obvious fix — hand the session a real NavUserPermissions so BC's own code runs — does
//   not survive contact with `HasRole`:
//
//     public bool HasRole(string companyName, PermissionSetKey permissionSet)
//         => GetRoles(tenant.Database.CompanyTokens.Get(companyName))
//            .Contains(new NavRole(permissionSet, tenant.Database));
//
//     public HashSet<NavRole> GetRoles(int companyNameToken) { ...; value = FetchRoles(companyNameToken); ... }
//
//   Both halves need a real SQL-backed `NavTenant.Database`: `FetchRoles` reads the permission
//   tables out of it, and `NavRole` is constructed against it. BcRuntime states plainly that
//   populating a skeleton NavTenant "pulls in the full database-bring-up chain and is out of
//   scope", having measured it breaking ~466 tests. So the answer has to be computed, and the
//   only honest place to compute it from is state the runner genuinely holds.
//
// WHAT IT IS COMPUTED FROM
//   The Access Control table (2000000053) — the same table real BC builds its permission cache
//   out of, and ordinary AL-writable storage in the runner. A row matches when its User
//   Security ID, Role ID, App ID and Scope all equal the ones asked about, and its Company Name
//   is either the company asked about or blank (a blank Company Name is BC's "every company"
//   assignment). That makes the answer real, observable and writable from AL in both
//   directions: assign a permission set and it reads back true, do not and it reads back false.
//
//   PLUS ONE STATED FACT ABOUT THE RUNNER ITSELF: the skeleton session is SUPER. That is not
//   invented here — it is the position this runner already ships, three times over, each with
//   the same one-line justification:
//
//     * NclCecilRewrite.Metadata.cs rewrites NavSession.HasExecutePermission /
//       HasCachedExecutePermissions / HasExecutePermissionForCompany /
//       HasExecutePermissionForAllCompanies → `true` ("skeleton session runs as SUPER").
//     * The same file no-ops NavSession.VerifyExecutePermission for the same reason.
//     * NclCecilRewrite.Runtime.cs no-ops NavSession.MaximizePermissions /
//       RemoveMaximizedPermissions ("The runner has no permission system (equivalent to SUPER
//       everywhere)"), and BcRuntime returns true from ALDatabase.ALSetUserPassword because
//       SessionHasSuperOrSecurityPermissionsForUser NREs on the skeleton.
//
//   Answering `IsSuper(UserSecurityId())` with `false` while `HasExecutePermission` answers
//   `true` would make the runner contradict itself, and would make codeunit 9002 refuse a
//   `User.Modify` that every real BC test tier allows, because a test tier's user is SUPER.
//   So the SUPER fact is stated ONCE, here, narrowly: it applies to the session's own user and
//   to the SUPER role only. It does not claim any OTHER permission set is assigned to the
//   session user — under SUPER, "D365 BASIC" is superseded, not assigned, and answering `true`
//   there would be exactly the silent fake .claude/rules/loud-failures.md forbids.
//
// KNOWN DIVERGENCE, DELIBERATE
//   Because the SUPER fact is stated rather than seeded, the runner's Access Control table does
//   NOT contain a row for the session user, while `IsSuper(UserSecurityId())` answers true. On
//   a real tier those agree. Seeding the row instead was considered and rejected for this
//   change: RecordPatches.UserSystemTable.cs's header records a deliberate decision not to seed
//   Access Control alongside the User row, and a seed would alter table state for every test in
//   every bundle, whereas stating the fact here can only change an answer that throws today.
//   AlRunner#3040 tracks the seed as the alternative, and docs/limitations.md records the
//   divergence.
//
// PRECOMPILED-DLL RESPECT
//   `PermissionManagement` is in Ncl.dll — the runtime engine, ours to rewrite per
//   .claude/rules/precompiled-dll-respect.md. No AL business-logic body is touched: codeunits
//   152/153/9002 run their own real bodies and simply get an answer instead of an NRE. The
//   rewrite is registered in NclCecilRewrite.Runtime.cs's cluster batch and imports only this
//   helper's memberRef, so no new Ncl typeRefs/memberRefs are added.
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Runtime.Permissions;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int AccessControlTableId = 2000000053;

    // Located by NAME off the metatable at runtime, never by hardcoded ordinal, so a BC
    // metadata change says so loudly instead of silently reading the wrong slot. Same
    // discipline as RecordPatches.UserSystemTable.
    private const string AcUserSecurityIdFieldName = "User Security ID";
    private const string AcRoleIdFieldName = "Role ID";
    private const string AcCompanyNameFieldName = "Company Name";
    private const string AcAppIdFieldName = "App ID";
    private const string AcScopeFieldName = "Scope";

    /// <summary>The SUPER role, the one permission set the skeleton session holds.</summary>
    private const string SuperRoleId = "SUPER";

    private static bool _accessControlMissingReported;

    /// <summary>
    /// Cecil-owned replacement for
    /// <c>Microsoft.Dynamics.Nav.Runtime.PermissionManagement.IsPermissionSetAssignedAsync</c>.
    /// See this file's header for the mechanism and for why the answer is computed rather than
    /// delegated back to BC.
    ///
    /// Synchronous by construction — every source it reads is in memory — so it hands back an
    /// already-completed <see cref="ValueTask{T}"/> rather than introducing a suspension point
    /// into a call chain the runner drives from one AL thread.
    /// </summary>
    public static ValueTask<bool> PermissionManagement_IsPermissionSetAssignedAsync(
        NavSession session, Guid userSecurityId, PermissionSetKey permissionSet, string companyName)
        => new(IsPermissionSetAssignedCore(session, userSecurityId, permissionSet, companyName));

    private static bool IsPermissionSetAssignedCore(
        NavSession session, Guid userSecurityId, PermissionSetKey permissionSet, string companyName)
    {
        // An explicit Access Control row wins, and is checked first, so AL that assigns a
        // permission set is always believed — including one that assigns SUPER to the session
        // user, which then needs no special case at all.
        if (AccessControlGrants(session, userSecurityId, permissionSet, companyName))
            return true;

        // The one stated fact: the skeleton session runs as SUPER. Narrow on purpose — this
        // user only, this role only. See the header.
        return IsSkeletonSessionUser(session, userSecurityId)
               && string.Equals(RoleIdOf(permissionSet), SuperRoleId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Is <paramref name="userSecurityId"/> the user this session actually is? Read from
    /// <c>NavSession.User</c> — the very property BC's own body compares against — so the
    /// runner cannot disagree with BC about who the current user is.
    /// </summary>
    private static bool IsSkeletonSessionUser(NavSession session, Guid userSecurityId)
    {
        try
        {
            var user = session?.User;
            return user != null && user.Id == userSecurityId;
        }
        catch (NullReferenceException)
        {
            // A session with no Authenticator at all is not "some other user" — it is no user.
            // Returning false keeps that case answering "not assigned" rather than claiming
            // SUPER for an identity that does not exist.
            return false;
        }
    }

    private static string RoleIdOf(PermissionSetKey permissionSet)
        => permissionSet.RoleId?.ToString() ?? string.Empty;

    /// <summary>
    /// Does the Access Control table hold a row assigning <paramref name="permissionSet"/> to
    /// <paramref name="userSecurityId"/> for <paramref name="companyName"/>?
    ///
    /// Company matching follows BC's own model: a row naming the company matches that company,
    /// and a row with a BLANK Company Name matches every company. A company-specific row does
    /// NOT satisfy a blank (all-companies) question — which is the question codeunit 153's
    /// <c>IsSuper</c> asks, since it passes <c>''</c>.
    /// </summary>
    private static bool AccessControlGrants(
        NavSession session, Guid userSecurityId, PermissionSetKey permissionSet, string companyName)
    {
        var meta = EnsureTableInMetadataCache(AccessControlTableId);
        if (meta == null)
        {
            // A bundle whose closure has no Access Control metatable cannot express an
            // assignment, so "not assigned" is the truthful answer rather than a default —
            // but say so once, because the alternative reading (a metadata regression that
            // silently emptied the table) looks identical from AL.
            if (!_accessControlMissingReported)
            {
                _accessControlMissingReported = true;
                Console.Error.WriteLine(
                    "[PermissionSetAssignment] this bundle's closure has no Access Control "
                    + $"metatable ({AccessControlTableId}), so no permission-set assignment can "
                    + "exist in it; every IsPermissionSetAssigned question about a user other "
                    + "than the session's own answers false. See AlRunner#3039.");
            }
            return false;
        }

        using var probe = new NavRecord(session, AccessControlTableId, SecurityFiltering.Ignored);
        var acMeta = probe.MetaTable ?? meta;

        var userField = FieldByNameOnAccessControl(acMeta, AcUserSecurityIdFieldName);
        var roleField = FieldByNameOnAccessControl(acMeta, AcRoleIdFieldName);
        var companyField = FieldByNameOnAccessControl(acMeta, AcCompanyNameFieldName);
        var appIdField = FieldByNameOnAccessControl(acMeta, AcAppIdFieldName);
        var scopeField = FieldByNameOnAccessControl(acMeta, AcScopeFieldName);

        probe.ALSetRange(userField.FieldNo, NavValue.CreateNavValueFromObject(userField, userSecurityId));
        probe.ALSetRange(roleField.FieldNo, NavValue.CreateNavValueFromObject(roleField, RoleIdOf(permissionSet)));
        probe.ALSetRange(appIdField.FieldNo, NavValue.CreateNavValueFromObject(appIdField, permissionSet.AppId));
        probe.ALSetRange(scopeField.FieldNo, NavValue.CreateNavValueFromObject(scopeField, (int)permissionSet.Scope));

        // CS0618: the synchronous entry points are marked obsolete in favour of the async ones.
        // This runs on the runner's single AL thread with no await point available in the call
        // chain — the same trade RecordPatches.UserSystemTable next door already makes.
#pragma warning disable CS0618
        if (!probe.ALFindFirstAsync(DataError.TrapError).GetAwaiter().GetResult())
            return false;
        do
        {
            var rowCompany = probe.GetFieldValue(companyField.FieldNo)?.ToString() ?? string.Empty;
            if (rowCompany.Length == 0
                || string.Equals(rowCompany, companyName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        while (probe.ALNextAsync().GetAwaiter().GetResult() != 0);
#pragma warning restore CS0618
        return false;
    }

    private static NCLMetaField FieldByNameOnAccessControl(NCLMetaTable table, string fieldName)
    {
        foreach (var f in GetAllFields(table) ?? Enumerable.Empty<NCLMetaField>())
            if (string.Equals(f.FieldName, fieldName, StringComparison.OrdinalIgnoreCase))
                return f;
        throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
            $"{table.TableName} ({table.TableId})",
            $"permission-set-assignment — the table states no \"{fieldName}\" field, so whether a "
            + "permission set is assigned cannot be answered from it; see docs/scope.md");
    }
}
