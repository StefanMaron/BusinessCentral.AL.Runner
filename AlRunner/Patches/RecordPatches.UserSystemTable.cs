// RecordPatches.UserSystemTable — put the runner's own session user in the User table.
//
// WHY THIS EXISTS (AlRunner#2296)
//   BcRuntime builds a skeleton NavUser with userName = "TESTUSER" and the deterministic
//   security id {C0A1BDFA-0000-0000-0000-545553545553}, and field-pokes it onto the skeleton
//   NavSession's Authenticator. That is what makes AL's UserId() and UserSecurityId() answer.
//   What it does NOT do is put a matching ROW in the User system table (2000000120), because
//   that table is ordinary storage rather than session state.
//
//   So the runner handed AL a user id, AL stored it, and then AL's own referential check could
//   not find that user anywhere:
//
//       NavCSideValidateTableRelationException: The field User SID of table User Personalization
//       contains a value ({C0A1BDFA-0000-0000-0000-545553545553}) that cannot be found in the
//       related table (User).
//
//   Same shape as #2329 (the Company row), and the same reason: in real BC nothing seeds either
//   row from AL — the platform creates the user when the account is provisioned, before any AL
//   runs. The runner has no user-provisioning step, so the table stayed empty of the one user
//   every session actually is.
//
//   `--test-data` does not cause this and does not fix it. A hydrated backup fills User with the
//   backup's users, none of which is the runner's synthetic one; it only makes the gap more
//   visible, because more code paths get far enough to write a row carrying a user id.
//
// WHY IT ONLY STARTED FAILING IN BULK ON 2026-09-05
//   Measured, not inferred: this suite's positive test fails on 10362356 (#2781, "read a
//   precompiled table's TableRelation from SymbolReference.json") and PASSES on its parent
//   6fa0fc2a, where the relation on User Personalization."User SID" was never read and any
//   value was silently accepted — including one belonging to nobody. #2781 made the runner
//   correct; the missing row is the older gap it exposed. On a Microsoft Tests-SMB bucket run
//   (BC 28.1.49838.53910, --test-data) the cluster is 62 tests.
//
// WHAT THIS DOES
//   Inserts exactly one row, whose values are read back from the skeleton NavUser BcRuntime
//   already built, so the session and the table cannot disagree about who the user is:
//
//     User Security ID ← NavUser.userGuid   (what AL's UserSecurityId() returns)
//     User Name        ← NavUser.userName   (what AL's UserId() returns)
//     Full Name        ← NavUser.fullName
//
//   Every other column keeps the default NavRecord initialisation gives it, which is already
//   what BC's own defaults are for the two that matter: State = Enabled and License Type =
//   "Full User". Nothing invents an authentication email, a Windows SID or an application id —
//   the runner's user has none of those, and writing a plausible-looking value would be the
//   silent-fake this repo's loud-failures rule forbids.
//
//   The insert goes through NavRecord.ALInsert rather than straight at the data provider (the
//   way the Company row's seed does), for one reason: BC's platform creates a User Property
//   (2000000121) row alongside every User, and UserTableTriggerPatches already reproduces that
//   as a Cecil prepend on NavRecord's AL insert entry point. Going through the same entry point
//   the session user gets its companion row for free, so it is a complete user rather than a
//   User row with the invariant #2355 established broken for it alone.
//
//   It runs once per bundle, immediately before CaptureInstallBaseline(), so the row is part of
//   the committed baseline every test is restored to. That ordering is the whole point: seeded
//   after the baseline, the first codeunit boundary would drop it again.
//
//   With --test-data armed, reaching the table through NavRecord also fires the on-demand
//   loader first (RecordPatches.GetDataAccessForTableCore calls it the moment it creates fresh
//   storage), so the backup's users land BEFORE this row rather than being loaded over it.
//
// WHAT THIS DELIBERATELY DOES NOT DO
//   It does not seed User Setup (91), Access Control (2000000053) or User Personalization
//   (2000000073) for this user. Real BC does not create any of those with the account either —
//   they are application-level rows created when someone configures them — so seeding them
//   would be inventing state BC does not have. Cascade behaviour on delete/rename across those
//   tables is #2356, and remains out of this file.
//
// WHEN THE INSERT IS REFUSED (#2941 review)
//   The insert uses DataError.TrapError, whose entire purpose is to report a refusal as a
//   `false` return rather than an exception. Discarding that bool made "the row is now there"
//   and "the insert was refused and there is no row" indistinguishable, logged neither, and
//   marked the bundle seeded either way. The three outcomes are now separated
//   (UserRowSeedOutcome), a refusal is reported loudly with the colliding row named, and
//   _userRowSeededForThisBundle stays false when no row was written.
//
//   WHICH REFUSALS ARE REACHABLE, MEASURED. The review that found this predicted the bite would
//   be a UNIQUE KEY on "User Name": a --test-data backup carrying its own TESTUSER would refuse
//   the insert and leave no row for the runner's security id. Measured on BC 28.1.49838.53910
//   (AlRunner.Tests/Fixtures/SessionUserRowNameCollision) the seed lands anyway, and the
//   mechanism is not the one predicted either way:
//
//     * The runner's store for this table is BC's own CreateTempDataAccess, which enforces the
//       PRIMARY key and nothing else.
//     * On a real tier the duplicate user name is refused by a TRIGGER, not an index. Ncl's
//       SystemTableTriggers.OnBeforeInsertAsync `case 2000000120:` arm validates a unique user
//       name (with the Windows SID, authentication email and application id) before writing.
//       UserTableTriggerPatches's own header records that the runner reproduces exactly one
//       thing from that arm — its User Property companion insert — and that "None of that
//       [validation] is reproduced here".
//
//   So the run is left holding two rows sharing a user name where BC would hold one
//   (AlRunner#2983, with the reproducer). Today's reachable refusal is the primary-key one,
//   which is the benign AlreadyPresent case; Refused is reached only from the exception path.
//   It is implemented anyway: the bug was discarding the signal, and #2983 — closed by
//   reproducing the trigger's validation or by enforcing uniqueness in the store — is exactly
//   what would make Refused reachable from AL. That fixture is the canary for the moment it is.
//
// PRECOMPILED-DLL RESPECT
//   No AL business-logic body is touched. NavRecord, NCLMetaTable, NCLMetaField and NavValue are
//   runtime-engine types, and the row is inserted through BC's own AL insert entry point exactly
//   as AL code would.
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int UserSystemTableId = 2000000120;

    // Located by NAME off the metatable at runtime, never by a hardcoded ordinal, so a BC
    // metadata change says so instead of silently writing into the wrong slot.
    private const string UserSecurityIdFieldName = "User Security ID";
    private const string UserNameFieldName = "User Name";
    private const string UserFullNameFieldName = "Full Name";


    /// <summary>
    /// What a call to <see cref="EnsureUserSystemTableRowSeeded"/> actually achieved.
    ///
    /// This enum exists because of the #2941 review. The insert goes through
    /// <c>ALInsert(DataError.TrapError, …)</c>, and TrapError's whole job is to convert a
    /// refusal into a <c>false</c> return instead of an exception — so "the row is now there"
    /// and "the insert was refused and there is no row" have exactly the same shape unless
    /// somebody looks. Discarding that <c>bool</c> made the two indistinguishable, logged
    /// neither, and marked the bundle seeded either way.
    ///
    /// Which refusals are reachable is measured, not assumed. The runner's store for this table
    /// is BC's own <c>CreateTempDataAccess</c>, which enforces the primary key only; and real
    /// BC refuses a duplicate user name from its system-table TRIGGER
    /// (<c>SystemTableTriggers.OnBeforeInsertAsync</c>, <c>case 2000000120:</c>), which
    /// <c>UserTableTriggerPatches</c> deliberately does not reproduce. So today a same-named
    /// foreign user does NOT refuse this insert (AlRunner#2983), and <see cref="Refused"/> is
    /// reached only from the exception path. It is implemented regardless: the defect was
    /// discarding the signal, and #2983 is exactly what makes the second refusal reachable.
    /// </summary>
    internal enum UserRowSeedOutcome
    {
        /// <summary>Already settled earlier in this bundle; this call did nothing.</summary>
        AlreadySeededThisBundle,
        /// <summary>This bundle's closure has no User metatable, so there is nothing to seed.</summary>
        NoUserTable,
        /// <summary>The skeleton session exposes no user identity to seed the row FROM.</summary>
        NoSessionIdentity,
        /// <summary>This call wrote the row.</summary>
        Inserted,
        /// <summary>A row for the session user's own security id was already in the table.</summary>
        AlreadyPresent,
        /// <summary>The insert was refused AND no row for the session user's security id exists.</summary>
        Refused,
    }

    private static bool _userRowSeededForThisBundle;
    private static bool _userRowSeedInProgress;

    internal static void ResetUserSystemTableForNewBundle() => _userRowSeededForThisBundle = false;

    /// <summary>
    /// True only when the User table actually holds a row for the session user's security id.
    /// Deliberately NOT set by a refusal: a flag that reads "seeded" over an empty table is the
    /// silent-wrong-answer this file's own loud-failures obligation forbids.
    /// </summary>
    internal static bool UserRowSeededForThisBundle => _userRowSeededForThisBundle;

    /// <summary>
    /// Insert the runner's own session user into the User system table (2000000120), once per
    /// bundle. Call AFTER install triggers and BEFORE <c>CaptureInstallBaseline()</c>, so the
    /// row is part of the restored baseline.
    /// </summary>
    /// <returns>
    /// Which of the outcomes in <see cref="UserRowSeedOutcome"/> this call reached. The
    /// production call site ignores it; the point of returning it is that the three insert
    /// outcomes are separable at all — see the enum's own remarks.
    /// </returns>
    internal static UserRowSeedOutcome EnsureUserSystemTableRowSeeded()
    {
        if (_userRowSeededForThisBundle) return UserRowSeedOutcome.AlreadySeededThisBundle;
        // The flag is no longer set before the work, so the insert below — which re-enters
        // NavRecord and, through UserTableTriggerPatches, a second table — is now inside the
        // window where re-entry would recurse. This guard closes it explicitly rather than
        // relying on there being no such call path today.
        if (_userRowSeedInProgress) return UserRowSeedOutcome.AlreadySeededThisBundle;
        _userRowSeedInProgress = true;
        try
        {
            return SeedUserRowCore();
        }
        finally
        {
            _userRowSeedInProgress = false;
        }
    }

    private static UserRowSeedOutcome SeedUserRowCore()
    {
        var meta = EnsureTableInMetadataCache(UserSystemTableId);
        if (meta == null)
            // A bundle whose closure has no User metatable has no user concept to seed, and
            // nothing in it can carry a relation to one either. Same shape as the Company
            // seed's "no Company metatable in this bundle" early return.
            return UserRowSeedOutcome.NoUserTable;

        var session = AlRunner.BcRuntime.SkeletonSession;
        if (session == null)
        {
            // #3068: `[warn]`, not `[UserSystemTable]` — Log.cs suppresses component tags at
            // default verbosity, which made "loud, never silent" untrue here for as long as this
            // carried one. Same for the two sibling branches below.
            // Loud, never silent: without this row every Validate of a field relating to
            // User."User Security ID" refuses the id the runner itself reports, and the failure
            // surfaces layers up inside Microsoft AL where it reads as an application bug.
            Console.Error.WriteLine(
                "[warn] UserSystemTable: there is no skeleton session, so the User row (2000000120) was "
                + "not seeded — every TableRelation to User.\"User Security ID\" will refuse "
                + "UserSecurityId(). See AlRunner#2296.");
            return UserRowSeedOutcome.NoSessionIdentity;
        }

        var (userName, fullName, userSid) = ReadSkeletonUserIdentity(session);
        if (userSid == null || userName == null)
        {
            Console.Error.WriteLine(
                "[warn] UserSystemTable: the skeleton session exposes no user identity "
                + $"(name={userName ?? "<null>"}, sid={(userSid == null ? "<null>" : "set")}), so the "
                + "User row (2000000120) was not seeded — every TableRelation to "
                + "User.\"User Security ID\" will refuse UserSecurityId(). See AlRunner#2296.");
            return UserRowSeedOutcome.NoSessionIdentity;
        }

        UserRowSeedOutcome outcome;
        string refusalDetail;
        try
        {
            outcome = InsertSessionUserRow(session, meta, userName, fullName, userSid, out refusalDetail);
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
            // DataError.TrapError converts the ordinary refusals into a false return, so an
            // exception reaching here is something else entirely — including the
            // NavRecordAlreadyExistsException a non-trapping path would raise.
            if (inner.GetType().Name == "NavRecordAlreadyExistsException")
            {
                // Confirmed by the SAME Get the non-exception path uses, rather than inferred
                // from the exception's type name. This method's stated invariant is that
                // _userRowSeededForThisBundle is true only when a row for THIS security id is
                // actually present, and "something already existed" is not evidence of that —
                // it is evidence that something clashed. Setting the flag off the type name
                // alone would be the same shape of unchecked claim the discarded ALInsert bool
                // was, in the one branch that still made it.
                if (SessionUserRowExists(session, userSid))
                {
                    _userRowSeededForThisBundle = true;
                    PerfTrace.Log($"UserSystemTable: User row '{userName}' was already present");
                    return UserRowSeedOutcome.AlreadyPresent;
                }
                outcome = UserRowSeedOutcome.Refused;
                refusalDetail = $"{inner.GetType().Name}: {inner.Message} — and no row for the "
                    + "session user's own security id is present, so the clash was with some "
                    + "other row";
            }
            else
            {
                outcome = UserRowSeedOutcome.Refused;
                refusalDetail = $"{inner.GetType().Name}: {inner.Message}";
            }
        }

        switch (outcome)
        {
            case UserRowSeedOutcome.Inserted:
                _userRowSeededForThisBundle = true;
                PerfTrace.Log($"UserSystemTable: seeded User row '{userName}'");
                break;
            case UserRowSeedOutcome.AlreadyPresent:
                _userRowSeededForThisBundle = true;
                PerfTrace.Log($"UserSystemTable: User row '{userName}' was already present");
                break;
            default:
                // REFUSED, and the row is genuinely absent. Loud on stderr rather than thrown,
                // and the choice is deliberate:
                //
                //   * Throwing here aborts the whole app group. This runs once per bundle at
                //     install-seed time, before CaptureInstallBaseline and outside any test, so
                //     an exception takes down every test in the bundle — including the large
                //     majority that never touch a relation to User. On the --test-data bucket
                //     run this issue came from, a backup holding its own TESTUSER would turn a
                //     partial problem into a total outage.
                //   * Silence is what cost #2296 a bisect to diagnose, and is what this review
                //     finding is about.
                //   * Logging loudly loses nothing, because the tests that DO need the row
                //     still fail on their own, with BC's own
                //     NavCSideValidateTableRelationException. Nothing here can turn a failing
                //     test green — so loud-failures.md's "a green test would lie" hazard is not
                //     in play — and this line is what tells the reader why that exception is
                //     about to appear.
                //
                // _userRowSeededForThisBundle is deliberately NOT set: no row was seeded, and a
                // flag claiming otherwise is the defect this branch exists to remove.
                Console.Error.WriteLine(
                    $"[warn] UserSystemTable: the User row (2000000120) for the session user "
                    + $"'{userName}' ({userSid}) was REFUSED and is NOT present — {refusalDetail}. "
                    + "Every TableRelation to User.\"User Security ID\" will refuse the id "
                    + "UserSecurityId() itself returns, and Microsoft AL guarded by "
                    + "User.IsEmpty() will take its non-empty branch against a table that does "
                    + "not contain this session's user. See AlRunner#2296.");
                break;
        }
        return outcome;
    }

    /// <summary>
    /// The user identity BcRuntime already seeded onto the skeleton session's NavUser. Read
    /// back rather than recomputed, so the row and the session are the same user by
    /// construction — the same reasoning as ReadSkeletonCompanyIdentity next door.
    /// </summary>
    private static (string? Name, string? FullName, NavGuid? Sid) ReadSkeletonUserIdentity(object session)
    {
        const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;

        // NavSession.Authenticator → NavUserAuthentication.navUser → NavUser, which is the exact
        // chain ALDatabase.get_ALUserID and ALDatabase.ALUserSecurityId walk.
        var auth = session.GetType().GetProperty("Authenticator", F)?.GetValue(session);
        var navUser = auth?.GetType().GetField("navUser", F)?.GetValue(auth);
        if (navUser == null) return (null, null, null);

        var name = navUser.GetType().GetField("userName", F)?.GetValue(navUser) as string;
        var fullName = navUser.GetType().GetField("fullName", F)?.GetValue(navUser) as string;
        var sid = navUser.GetType().GetField("userGuid", F)?.GetValue(navUser) as NavGuid;
        return (name, fullName, sid);
    }

    private static UserRowSeedOutcome InsertSessionUserRow(
        object session, NCLMetaTable meta, string userName, string? fullName, NavGuid userSid,
        out string refusalDetail)
    {
        refusalDetail = string.Empty;

        using var user = new NavRecord((NavSession)session, UserSystemTableId, SecurityFiltering.Ignored);
        var userMeta = user.MetaTable ?? meta;

        user.SetFieldValue(FieldNoByNameOnUser(userMeta, UserSecurityIdFieldName), userSid);
        user.SetFieldValue(
            FieldNoByNameOnUser(userMeta, UserNameFieldName),
            NavValue.CreateNavValueFromObject(FieldByNameOnUser(userMeta, UserNameFieldName), userName));
        if (!string.IsNullOrEmpty(fullName))
            user.SetFieldValue(
                FieldNoByNameOnUser(userMeta, UserFullNameFieldName),
                NavValue.CreateNavValueFromObject(FieldByNameOnUser(userMeta, UserFullNameFieldName), fullName));

        // TrapError so a run that already has this user (a resumed app group, a bundle whose
        // install code created it, a --test-data backup carrying it) leaves the existing row
        // alone rather than turning the seed into a duplicate-key failure.
        // runApplicationTrigger: false matches BC's own platform-level user creation, which
        // does not run AL triggers.
        //
        // The RESULT IS NOT DISCARDED (#2941 review). TrapError's contract is "report the
        // refusal as false instead of raising", so throwing the bool away is throwing away the
        // only signal there is.
        //
        // CS0618: BC marks the synchronous ALInsert obsolete in favour of the async form. This
        // runs on the runner's single AL thread with no await point available, the same trade
        // UserTableTriggerPatches next door already makes.
#pragma warning disable CS0618
        var inserted = user.ALInsert(DataError.TrapError, runApplicationTrigger: false, insertWithSystemId: false);
#pragma warning restore CS0618
        if (inserted) return UserRowSeedOutcome.Inserted;

        // Refused. Ask the TABLE which refusal this was, rather than assuming the benign one:
        // the primary key is "User Security ID", so a Get on the session user's own sid answers
        // exactly the question that matters — is the row this seed exists to guarantee there?
        if (SessionUserRowExists(session, userSid)) return UserRowSeedOutcome.AlreadyPresent;

        refusalDetail = DescribeUserRowRefusal(session, userMeta, userName);
        return UserRowSeedOutcome.Refused;
    }

    /// <summary>
    /// Is there a User row whose primary key is <paramref name="userSid"/>? This is the whole
    /// difference between "already present" and "refused": the seed's promise is a row for THIS
    /// security id, not merely that the table is non-empty.
    /// </summary>
    private static bool SessionUserRowExists(object session, NavGuid userSid)
    {
        using var probe = new NavRecord((NavSession)session, UserSystemTableId, SecurityFiltering.Ignored);
        // CS0618: same sync-over-async trade as the ALInsert above — one AL thread, no await
        // point available in this call chain.
#pragma warning disable CS0618
        return probe.ALGet(DataError.TrapError, userSid);
#pragma warning restore CS0618
    }

    /// <summary>
    /// Name the reason the insert was refused, for the stderr line. The case worth identifying
    /// explicitly, rather than reporting as an unexplained refusal, is a second user already
    /// holding this user name — what a <c>--test-data</c> backup carrying its own TESTUSER
    /// produces.
    ///
    /// On a real tier that name collision is refused by BC's system-table TRIGGER, not by an
    /// index: Ncl's <c>SystemTableTriggers.OnBeforeInsertAsync</c> <c>case 2000000120:</c> arm
    /// validates a unique user name (along with the Windows SID, authentication email and
    /// application id) before writing. <c>UserTableTriggerPatches</c>'s own header records that
    /// the runner reproduces only that arm's User Property companion insert and none of its
    /// validation, so the runner does not refuse the duplicate here at all. This text therefore
    /// describes what BC would do, and says plainly that the runner did something else.
    /// </summary>
    private static string DescribeUserRowRefusal(object session, NCLMetaTable userMeta, string userName)
    {
        try
        {
            using var probe = new NavRecord((NavSession)session, UserSystemTableId, SecurityFiltering.Ignored);
            var nameField = FieldByNameOnUser(userMeta, UserNameFieldName);
            probe.ALSetRange(nameField.FieldNo, NavValue.CreateNavValueFromObject(nameField, userName));
            if (probe.ALFindFirstAsync(DataError.TrapError).GetAwaiter().GetResult())
            {
                var otherSid = probe.GetFieldValue(FieldNoByNameOnUser(userMeta, UserSecurityIdFieldName));
                return $"the table already holds a DIFFERENT user named \"{userName}\" whose "
                    + $"User Security ID is {otherSid} (a --test-data backup carrying its own user "
                    + "of this name does exactly this). On a real tier BC refuses that collision in "
                    + "SystemTableTriggers.OnBeforeInsertAsync's case 2000000120: arm, which "
                    + "validates a unique user name before writing — NOT in a unique index. The "
                    + "runner reproduces only that arm's User Property companion insert and none of "
                    + "its validation (see AlRunner/Patches/UserTableTriggerPatches.cs), so if this "
                    + "line is being printed the refusal came from somewhere else and is worth "
                    + "reading closely";
            }
            return "no row holds the session user's security id and no other row holds its user "
                + "name either, so the refusal came from neither the primary key nor a name "
                + "collision";
        }
        catch (Exception ex)
        {
            // Diagnosis only — the refusal itself is already being reported by the caller, so a
            // failure to explain it must not replace that report with a second exception.
            var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
            return $"the reason could not be determined ({inner.GetType().Name}: {inner.Message})";
        }
    }

    /// <summary>
    /// The metafield <paramref name="fieldName"/> names on the User table. Throws rather than
    /// returning a sentinel: a missing field means the row would be written with the wrong
    /// shape, which is the silent-wrong-answer case .claude/rules/loud-failures.md forbids.
    /// </summary>
    private static NCLMetaField FieldByNameOnUser(NCLMetaTable table, string fieldName)
    {
        foreach (var f in GetAllFields(table) ?? Enumerable.Empty<NCLMetaField>())
            if (string.Equals(f.FieldName, fieldName, StringComparison.OrdinalIgnoreCase))
                return f;
        throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
            $"{table.TableName} ({table.TableId})",
            $"session-user-row — the table states no \"{fieldName}\" field, so the User row the "
            + "runner's own session identity needs cannot be written; see docs/scope.md");
    }

    private static int FieldNoByNameOnUser(NCLMetaTable table, string fieldName)
        => FieldByNameOnUser(table, fieldName).FieldNo;
}
