// SEED THE ACCESS CONTROL ROW THAT BACKS THE SESSION USER'S SUPER STATUS (#3176)
//
// WHAT THIS FIXES
//   Before this file the runner answered `UserPermissions.IsSuper(UserSecurityId())` with true
//   while `Record "Access Control"` held NO row for that user. Those are two views of one fact
//   on a real tier, and the runner made them disagree.
//
//   That divergence was recorded deliberately in RecordPatches.PermissionSetAssignment.cs's
//   header and tracked as AlRunner#3176, which stated plainly that deciding it "needs a view on
//   whether the runner's synthetic user is 'provisioned' state (seed it) or 'configured' state
//   (do not)". A real service tier has now supplied that view. Corpus codeunit 60889
//   `SessionUserIsSuper_AndAccessControlHoldsTheRowThatSaysSo` (upstream PR #204, green on all
//   eight required BC legs) asserts both halves together:
//
//       UserPermissions.IsSuper(UserSecurityId())                       -> true
//       AccessControl.SetRange("User Security ID", UserSecurityId());
//       AccessControl.SetRange("Role ID", 'SUPER');
//       AccessControl.IsEmpty()                                         -> false
//
//   So SUPER status IS backed by a row, measured rather than reasoned. #3176's open question is
//   answered in favour of the seed, and this file is that seed.
//
// WHY THE SEED, AND NOT A WIDER STATED FACT
//   The alternative — keep stating the fact and additionally fake the table read — would be the
//   silent fake .claude/rules/loud-failures.md forbids: AL that lists the session user's
//   permission sets would still find none, while being told the user is SUPER. Seeding one real
//   row makes the answer come from ordinary AL-writable storage, so every reader agrees by
//   construction rather than by each one being special-cased. It also lets
//   IsPermissionSetAssignedCore's Access-Control arm — which is checked FIRST — carry the
//   session user's SUPER answer on its own, without reaching the stated fact at all.
//
//   The stated fact in PermissionSetAssignment.cs is deliberately LEFT IN PLACE. It is not
//   redundant: a bundle whose closure has no Access Control metatable, or one whose install code
//   refuses the seed, still needs IsSuper to answer true or codeunit 9002 refuses a `User.Modify`
//   every real BC test tier allows. The seed makes the table agree when the table exists; the
//   stated fact remains the floor when it does not.
//
// WHY IT IS SAFE TO SEED HERE, WHICH #3176 LISTED AS THE COST
//   #3176's objection was that "a seed changes table state for every test in every bundle and
//   becomes part of the install baseline". Both are true and both are intended — that is exactly
//   what makes the row survive the per-codeunit restore, the same reasoning
//   RecordPatches.UserSystemTable.cs gives for the User row it seeds one statement earlier. What
//   the objection was protecting against is a row appearing where a test counts rows; measured,
//   the corpus and runner-extras hold no test that asserts Access Control's row COUNT, and the
//   one test that reads the table at all is the corpus test above, which requires the row.
//
//   RecordPatches.UserSystemTable.cs's header says it "does not seed User Setup (91), Access
//   Control (2000000053) or User Personalization (2000000073) ... they are application-level rows
//   created when someone configures them". That reasoning stands for User Setup and User
//   Personalization and this file does not touch them. It does NOT stand for the SUPER row: a
//   tier's first user is SUPER by provisioning, before anyone configures anything, which is what
//   the corpus test measures. That header is corrected in this change rather than left to
//   contradict the file next to it.
//
// COLUMN VALUES, AND WHY EACH ONE
//   User Security ID  <- the session's own id, the same NavGuid the User row seed writes.
//   Role ID           <- 'SUPER'.
//   Company Name      <- BLANK. BC's "every company" assignment, and the only value that
//                        satisfies codeunit 153's IsSuper, which passes '' as the company.
//                        A company-specific row would NOT satisfy it — see
//                        AccessControlGrants' company-matching rules in the sibling file.
//   App ID            <- the null guid. SUPER is a platform permission set, not one an app
//                        contributes, and PermissionSetKey for it carries the null AppId.
//   Scope             <- 0 (System). Same reason: SUPER is a system-scoped set.
//
//   Nothing else is written. Access Control has no other columns the runner can honestly fill.
//
// PRECOMPILED-DLL RESPECT
//   No BC method body is touched. This writes a row through NavRecord.ALInsert, the same public
//   AL entry point ordinary AL uses, so the row is indistinguishable from one AL wrote itself.
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>What a call to <see cref="EnsureAccessControlSuperRowSeeded"/> achieved.</summary>
    internal enum AccessControlSeedOutcome
    {
        /// <summary>A row was inserted for the session user.</summary>
        Inserted,
        /// <summary>Already done for this bundle; nothing attempted.</summary>
        AlreadySeededThisBundle,
        /// <summary>This bundle's closure has no Access Control metatable.</summary>
        NoAccessControlTable,
        /// <summary>There is no skeleton session user to seed a row for.</summary>
        NoSessionIdentity,
        /// <summary>A row assigning SUPER to the session user was already present.</summary>
        AlreadyPresent,
        /// <summary>The insert was refused AND no such row exists.</summary>
        Refused,
    }

    /// <summary>
    /// The security id this bundle's SUPER row was written FOR, or null when no row is in the
    /// table. A plain bool cannot answer the question the seed is now asked twice per app group
    /// (#3757): the session can be moved onto an ADOPTED row between the two calls, and a latch
    /// that only remembers "a row exists" would leave the SUPER grant naming the id the session
    /// no longer has. See docs/session-user-seed-ordering.md.
    /// </summary>
    private static NavGuid? _accessControlSuperRowSeededFor;
    private static bool _accessControlRowSeedInProgress;

    internal static void ResetAccessControlSeedForNewBundle()
        => _accessControlSuperRowSeededFor = null;

    /// <summary>
    /// True only when Access Control actually holds a SUPER row for the session user. Not set by
    /// a refusal — a flag reading "seeded" over a table with no such row is the silent-wrong
    /// answer this file's loud-failures obligation forbids, the same discipline
    /// <see cref="EnsureUserSystemTableRowSeeded"/> follows.
    /// </summary>
    internal static bool AccessControlRowSeededForThisBundle => _accessControlSuperRowSeededFor != null;

    /// <summary>
    /// Insert the Access Control row that backs the session user's SUPER status, once per bundle.
    /// Call AFTER <see cref="EnsureUserSystemTableRowSeeded"/> — the row's "User Security ID"
    /// relates to User."User Security ID", so the User row must exist first — and BEFORE
    /// <c>CaptureInstallBaseline()</c>, so it is part of the baseline every test is restored to.
    /// </summary>
    internal static AccessControlSeedOutcome EnsureAccessControlSuperRowSeeded()
    {
        // NO short-circuit before the session identity is read: which id the row must name is
        // exactly what can have changed since the previous call (#3757). The settled-for-this-id
        // early return lives in the core below, after ReadSkeletonUserIdentity.
        //
        // Same re-entry guard as the User seed next door: the insert re-enters NavRecord, so the
        // window is closed explicitly rather than on there being no such path today.
        if (_accessControlRowSeedInProgress) return AccessControlSeedOutcome.AlreadySeededThisBundle;
        _accessControlRowSeedInProgress = true;
        try
        {
            return SeedAccessControlSuperRowCore();
        }
        finally
        {
            _accessControlRowSeedInProgress = false;
        }
    }

    private static AccessControlSeedOutcome SeedAccessControlSuperRowCore()
    {
        var meta = EnsureTableInMetadataCache(AccessControlTableId);
        if (meta == null)
            // A bundle with no Access Control metatable has no assignment concept to seed. The
            // stated SUPER fact in PermissionSetAssignment.cs still answers IsSuper for it, which
            // is precisely why that fact is not removed by this change. Silent on purpose: the
            // sibling file already reports this exact condition once, loudly, the first time an
            // assignment question is asked of such a bundle.
            return AccessControlSeedOutcome.NoAccessControlTable;

        var session = AlRunner.BcRuntime.SkeletonSession;
        if (session == null) return AccessControlSeedOutcome.NoSessionIdentity;

        var (_, _, userSid) = ReadSkeletonUserIdentity(session);
        if (userSid != null && _accessControlSuperRowSeededFor is { } seededFor)
        {
            if (string.Equals(seededFor.ToString(), userSid.ToString(), StringComparison.OrdinalIgnoreCase))
                return AccessControlSeedOutcome.AlreadySeededThisBundle;

            // The session moved onto an ADOPTED User row after this bundle's row was written
            // (#2983), so the grant names a security id the session no longer has — and, in the
            // adoption case, one whose User row the adopting data replaced. Withdraw the row this
            // seeder wrote before writing the new one: leaving it is a SUPER assignment for a user
            // that does not exist, which is the silent-wrong shape .claude/rules/loud-failures.md
            // forbids. Only ever the row this seeder wrote, never one install code contributed.
            var withdrawn = TryDeleteAccessControlSuperRow(session, meta, seededFor);
            PerfTrace.Log(
                $"AccessControlSeed: the session identity moved to {userSid}; the SUPER row for "
                + $"{seededFor} was {(withdrawn ? "withdrawn" : "already gone")}");
            _accessControlSuperRowSeededFor = null;
        }

        if (userSid == null)
        {
            // Loud, never silent — the same obligation the User seed carries. Without this row
            // the runner reports the session user SUPER while the table that is supposed to say
            // so is empty, which is the exact divergence this file exists to remove.
            Console.Error.WriteLine(
                "[warn] AccessControlSeed: the skeleton session exposes no user security id, so the "
                + $"SUPER row in Access Control ({AccessControlTableId}) was not seeded — "
                + "UserPermissions.IsSuper(UserSecurityId()) will answer true while the table holds "
                + "no row saying so. See AlRunner#3176.");
            return AccessControlSeedOutcome.NoSessionIdentity;
        }

        try
        {
            var outcome = InsertAccessControlSuperRow(session, meta, userSid);
            if (outcome is AccessControlSeedOutcome.Inserted or AccessControlSeedOutcome.AlreadyPresent)
            {
                _accessControlSuperRowSeededFor = userSid;
                PerfTrace.Log($"AccessControlSeed: SUPER row {outcome}");
                return outcome;
            }

            Console.Error.WriteLine(
                "[warn] AccessControlSeed: the SUPER row for the session user was refused and no such "
                + $"row is present in Access Control ({AccessControlTableId}). "
                + "UserPermissions.IsSuper(UserSecurityId()) still answers true from the stated fact in "
                + "RecordPatches.PermissionSetAssignment.cs, so AL guarding on IsSuper still works, but "
                + "AL that READS the table will find no assignment. See AlRunner#3176.");
            return AccessControlSeedOutcome.Refused;
        }
        catch (Exception ex)
        {
            // Never let a seed failure take the run down: the stated SUPER fact keeps IsSuper
            // answering, so a refused seed degrades to the pre-#3176 behaviour rather than to a
            // crash. Loud, because the divergence is back when this prints.
            Console.Error.WriteLine(
                $"[warn] AccessControlSeed: seeding the session user's SUPER row raised {ex.GetType().Name}: "
                + $"{ex.Message}. The Access Control table holds no row for the session user; "
                + "IsSuper still answers true from the stated fact. See AlRunner#3176.");
            return AccessControlSeedOutcome.Refused;
        }
    }

    private static AccessControlSeedOutcome InsertAccessControlSuperRow(
        object session, NCLMetaTable meta, NavGuid userSid)
    {
        using var ac = new NavRecord((NavSession)session, AccessControlTableId, SecurityFiltering.Ignored);
        var acMeta = ac.MetaTable ?? meta;

        var userField = FieldByNameOnAccessControl(acMeta, AcUserSecurityIdFieldName);
        var roleField = FieldByNameOnAccessControl(acMeta, AcRoleIdFieldName);
        var companyField = FieldByNameOnAccessControl(acMeta, AcCompanyNameFieldName);
        var appIdField = FieldByNameOnAccessControl(acMeta, AcAppIdFieldName);
        var scopeField = FieldByNameOnAccessControl(acMeta, AcScopeFieldName);

        ac.SetFieldValue(userField.FieldNo, userSid);
        ac.SetFieldValue(roleField.FieldNo, NavValue.CreateNavValueFromObject(roleField, SuperRoleId));
        // Blank company: BC's "every company" assignment, and the only value codeunit 153's
        // IsSuper — which passes '' — is satisfied by. See the header.
        ac.SetFieldValue(companyField.FieldNo, NavValue.CreateNavValueFromObject(companyField, string.Empty));
        ac.SetFieldValue(appIdField.FieldNo, NavValue.CreateNavValueFromObject(appIdField, Guid.Empty));
        ac.SetFieldValue(scopeField.FieldNo, NavValue.CreateNavValueFromObject(scopeField, 0));

        // TrapError so a bundle whose install code already assigned SUPER to this user leaves the
        // existing row alone instead of turning the seed into a duplicate-key failure — the same
        // trade the User row seed makes. runApplicationTrigger: false matches BC's own
        // platform-level provisioning, which does not run AL triggers.
        //
        // CS0618: BC marks the synchronous ALInsert obsolete in favour of the async form. One AL
        // thread, no await point available in this chain — same trade as the sibling seeds.
#pragma warning disable CS0618
        var inserted = ac.ALInsert(DataError.TrapError, runApplicationTrigger: false, insertWithSystemId: false);
#pragma warning restore CS0618
        if (inserted) return AccessControlSeedOutcome.Inserted;

        // Refused. Ask the TABLE which refusal this was rather than assuming the benign one —
        // the seed's promise is a SUPER row for THIS user, not merely that the insert did not
        // raise.
        return AccessControlSuperRowExists(session, meta, userSid)
            ? AccessControlSeedOutcome.AlreadyPresent
            : AccessControlSeedOutcome.Refused;
    }

    /// <summary>
    /// Is there an all-companies SUPER row for <paramref name="userSid"/>? This is the whole
    /// difference between "already present" and "refused".
    /// </summary>
    private static bool AccessControlSuperRowExists(object session, NCLMetaTable meta, NavGuid userSid)
    {
        using var probe = new NavRecord((NavSession)session, AccessControlTableId, SecurityFiltering.Ignored);
        var acMeta = probe.MetaTable ?? meta;
        var userField = FieldByNameOnAccessControl(acMeta, AcUserSecurityIdFieldName);
        var roleField = FieldByNameOnAccessControl(acMeta, AcRoleIdFieldName);

        probe.ALSetRange(userField.FieldNo, userSid);
        probe.ALSetRange(roleField.FieldNo, NavValue.CreateNavValueFromObject(roleField, SuperRoleId));
#pragma warning disable CS0618
        return probe.ALFindFirstAsync(DataError.TrapError).GetAwaiter().GetResult();
#pragma warning restore CS0618
    }

    /// <summary>
    /// Delete the all-companies SUPER row for <paramref name="userSid"/>, if one is there.
    /// Narrow on purpose: the same filter the seed's own insert and probe use, so the only row
    /// this can remove is the shape this seeder writes. Returns whether a row was deleted.
    /// </summary>
    private static bool TryDeleteAccessControlSuperRow(object session, NCLMetaTable meta, NavGuid userSid)
    {
        try
        {
            using var stale = new NavRecord((NavSession)session, AccessControlTableId, SecurityFiltering.Ignored);
            var acMeta = stale.MetaTable ?? meta;
            var userField = FieldByNameOnAccessControl(acMeta, AcUserSecurityIdFieldName);
            var roleField = FieldByNameOnAccessControl(acMeta, AcRoleIdFieldName);

            stale.ALSetRange(userField.FieldNo, userSid);
            stale.ALSetRange(roleField.FieldNo, NavValue.CreateNavValueFromObject(roleField, SuperRoleId));
#pragma warning disable CS0618
            if (!stale.ALFindFirstAsync(DataError.TrapError).GetAwaiter().GetResult()) return false;
            return stale.ALDelete(DataError.TrapError, runApplicationTrigger: false);
#pragma warning restore CS0618
        }
        catch (Exception ex)
        {
            // Never take the run down for a withdrawal: the new row is still written below, so
            // the worst outcome is the stale grant the message names.
            Console.Error.WriteLine(
                $"[warn] AccessControlSeed: withdrawing the SUPER row for {userSid} raised "
                + $"{ex.GetType().Name}: {ex.Message}. Access Control may still hold a SUPER "
                + "assignment for a user security id the session no longer has. See AlRunner#3757.");
            return false;
        }
    }
}
