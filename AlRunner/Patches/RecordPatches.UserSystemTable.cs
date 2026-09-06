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
//
//   UPDATED by #2983: UserTableTriggerPatches now DOES reproduce that arm's two uniqueness
//   refusals — the user name (field 2) and the non-empty Windows Security ID (field 7) — through
//   BC's own exception factories. So a same-named foreign user now refuses this insert, and
//   Refused is reachable from AL rather than only from the exception path. The seed's own
//   RowWithSameSecurityIdExists skip keeps the session user itself out of that refusal.
//   tests/runner-extras/user-system-table-triggers measures both refusals and their controls.
//   Still NOT reproduced from the same arm: ValidateAuthenticationEmailAsync and
//   ValidateApplicationIdAsync (#2363's subject) — see UserTableTriggerPatches's header.
//
// WHAT THE SEED DOES WITH THAT REFUSAL — ADOPT (maintainer decision, 2026-09-06)
//   Refusing is right about the ROW: BC will not hold two users of one name, so the seed cannot
//   write its own. It left open what the SESSION should be, and the first implementation of
//   #2983 answered "a user that is in no row" — which is the state #2296 exists to remove.
//
//   The decision is to ADOPT. When exactly one existing User row carries this session's user
//   NAME, the session takes that row's security id as its own: UserSecurityId() returns it for
//   the rest of the run, and the seed writes nothing. That is what someone pointing --test-data
//   at a backup containing their own TESTUSER is asking for, and it is what a real tier does —
//   an authenticated session gets the security id of the user it authenticated AS, never a
//   synthetic one.
//
//   THE COST, WHICH IS WHY IT IS LOUD. UserSecurityId() now depends on the contents of a backup
//   file. AL asserting session identity sees one value with --test-data and another without it,
//   and nothing in the AL says why. That is the data-dependent-behaviour shape loud-failures.md
//   is about, so every adoption prints a [warn] line naming the user, the adopted id, the
//   generated id it replaced, and where it came from. [warn] and not a [Component] tag: Log.cs
//   suppresses component tags at default verbosity (#3068).
//
//   WHAT STILL REFUSES, LOUDLY. No row under that name (the refusal was not a name collision, so
//   there is nothing to adopt), and MORE THAN ONE row under it (real BC cannot hold that, so the
//   data is inconsistent and choosing between them would be a coin toss inside UserSecurityId()).
//   None of UserTableTriggerPatches's BC-faithful refusals is softened — an AL Insert of a
//   duplicate user name or Windows SID still raises BC's own exception, unchanged.
//
//   ORDERING CONSEQUENCE, NOT FIXED HERE. This runs AFTER the bundle's install triggers, so AL
//   that called UserSecurityId() during install saw the generated id, and any row it keyed on
//   that id keeps pointing at a user the session no longer is. Nothing in the runner's own seeds
//   does this, and nothing measured has hit it, but it is a real window and it is named rather
//   than papered over. Closing it means adopting before install triggers run, which forces the
//   User table's on-demand backup load earlier — inside the install-baseline caching window —
//   and that is a larger change than this one. See AlRunner#2983.
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
    /// is BC's own <c>CreateTempDataAccess</c>, which enforces the primary key only; real BC
    /// refuses a duplicate user name from its system-table TRIGGER
    /// (<c>SystemTableTriggers.OnBeforeInsertAsync</c>, <c>case 2000000120:</c>), and since
    /// #2983 <c>UserTableTriggerPatches</c> reproduces that refusal. So a same-named foreign
    /// user DOES refuse this insert now, and <see cref="Refused"/> is reachable from AL and not
    /// only from the exception path — which is what this enum was built to be able to say.
    /// The seed skips the refusal for a row that already carries this security id
    /// (<c>RowWithSameSecurityIdExists</c>), so seeding the session user stays idempotent.
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
        /// <summary>
        /// The insert was refused by a NAME collision, and the session took the colliding row's
        /// security id as its own instead of going without a row. See
        /// <see cref="TryAdoptSessionUserSecurityId"/> for why this is an adoption rather than a
        /// refusal, and what it is deliberately NOT extended to.
        /// </summary>
        AdoptedExistingRow,
        /// <summary>The insert was refused AND no row for the session user's security id exists.</summary>
        Refused,
    }

    private static bool _userRowSeededForThisBundle;
    private static bool _userRowSeedInProgress;

    /// <summary>
    /// The security id BcRuntime generated for the skeleton session, saved the first time an
    /// adoption overwrites it so the next bundle can be given it back. Null until then.
    ///
    /// <para>WHY THIS EXISTS. The seed's bundle flag resets per bundle
    /// (<see cref="ResetUserSystemTableForNewBundle"/>), but the skeleton NavUser adoption pokes
    /// is built ONCE PER PROCESS — in <c>BcRuntime.ApplyAllPatches</c>, on the BC load path.
    /// Without this, an adoption made for bundle A would still be the session identity when
    /// bundle B ran: multi-bundle runs, <c>--watch</c> and <c>--server</c> all execute several
    /// bundles in one process, so bundle B would silently inherit a security id that came out of
    /// bundle A's data and matches nothing in its own. A wrong answer with nothing to notice it
    /// by, which is precisely what adoption is not allowed to introduce.</para>
    /// </summary>
    private static NavGuid? _generatedSessionUserSid;

    internal static void ResetUserSystemTableForNewBundle()
    {
        _userRowSeededForThisBundle = false;

        // Put the generated identity back before the next bundle seeds, so each bundle decides
        // adoption against its OWN data from the same starting point. Idempotent and free when
        // nothing was ever adopted.
        if (_generatedSessionUserSid != null)
        {
            var session = AlRunner.BcRuntime.SkeletonSession;
            if (session != null && TryPokeSessionUserSecurityId(session, _generatedSessionUserSid))
                PerfTrace.Log(
                    "UserSystemTable: restored the generated session security id for the next bundle");
            _generatedSessionUserSid = null;
        }
    }

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

        // #2983, MAINTAINER DECISION 2026-09-06 — ADOPT, do not refuse.
        //
        // The refusal above is correct about BC: two users cannot share a name, so the seed
        // genuinely cannot write its row. What was left open was what the SESSION should then be.
        // Refusing leaves the run with a session user that exists nowhere in the User table,
        // which is the state #2296 was filed to remove; adopting makes the session BE the user
        // the data already describes, which is what a person pointing --test-data at a backup
        // containing their own TESTUSER is asking for. The maintainer chose adoption.
        //
        // The objection that survives the decision is about SILENCE, not about adopting:
        // UserSecurityId() now returns a value that came out of a backup file, and test code
        // asserting session identity sees a different answer with and without --test-data. So
        // TryAdoptSessionUserSecurityId reports every adoption on stderr at [warn], naming the
        // user, the adopted id, the generated id it replaced and where it came from. Nothing
        // about this may be quiet.
        //
        // Placed here rather than inside InsertSessionUserRow because BOTH refusal paths have to
        // reach it: the `inserted == false` return AND the catch above, which is where BC's own
        // NavNCLUserTableUserNameMustBeUniqueException actually arrives (TrapError converts data
        // errors, not a system-table trigger's raise).
        if (outcome == UserRowSeedOutcome.Refused)
        {
            if (TryAdoptSessionUserSecurityId(session, meta, userName, userSid, out var whyNotAdopted))
                outcome = UserRowSeedOutcome.AdoptedExistingRow;
            else
                refusalDetail = $"{refusalDetail}. It was not adopted either: {whyNotAdopted}";
        }

        switch (outcome)
        {
            case UserRowSeedOutcome.Inserted:
                _userRowSeededForThisBundle = true;
                PerfTrace.Log($"UserSystemTable: seeded User row '{userName}'");
                break;
            case UserRowSeedOutcome.AdoptedExistingRow:
                // The flag's stated invariant — "the User table holds a row for the session
                // user's security id" — is satisfied, and satisfied more directly than by the
                // insert: the session user's security id IS that row's, because the session was
                // moved onto it. Nothing was written.
                _userRowSeededForThisBundle = true;
                PerfTrace.Log($"UserSystemTable: adopted the existing User row for '{userName}'");
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
    /// The session-user seed was refused. If the reason is that the User table already holds
    /// EXACTLY ONE row carrying this session's user NAME under a different security id, move the
    /// session onto that row — its security id becomes what <c>UserSecurityId()</c> returns for
    /// the rest of the run — and report it loudly. Returns false, with a reason, in every other
    /// case.
    ///
    /// <para>WHY ADOPT AT ALL (#2983, maintainer decision 2026-09-06). A <c>--test-data</c>
    /// backup that carries its own <c>TESTUSER</c> is the common case this arises from, and the
    /// person who pointed the runner at that backup means for the session to be that user. The
    /// alternative shipped first — refuse, and run with a session user that exists in no row —
    /// is exactly the state #2296 was filed to remove, and it re-broke every TableRelation to
    /// <c>User."User Security ID"</c> for that run. Adopting is also what makes the runner agree
    /// with BC rather than merely refuse like BC: on a real tier, a session authenticated as a
    /// user that IS in the table gets that user's security id, and never a synthetic one.</para>
    ///
    /// <para>WHY IT IS LOUD, AND WHY THAT IS NOT NEGOTIABLE. Adopting makes
    /// <c>UserSecurityId()</c> depend on the contents of a backup file: AL asserting session
    /// identity sees one value with <c>--test-data</c> and another without it. That is the
    /// data-dependent-behaviour shape <c>.claude/rules/loud-failures.md</c> exists to prevent,
    /// and the answer is not to refuse the adoption but to make sure nobody has to wonder where
    /// the value came from. The <c>[warn]</c> line below names the user, the adopted id, the
    /// generated id it replaced, and the fact that it came from the data. <c>[warn]</c> rather
    /// than a <c>[Component]</c> tag because <c>Log.cs</c> suppresses component tags at default
    /// verbosity — #3068, and the precedent where exactly this kind of line was eaten by the
    /// component filter and cost 42 tests.</para>
    ///
    /// <para>WHAT STILL REFUSES, and the distinction is the point of the counting below:</para>
    /// <list type="bullet">
    ///   <item>NO row carries the session user's name — the refusal came from somewhere other
    ///   than a name collision (a Windows SID clash, say), so there is no row to adopt and no
    ///   basis for guessing one. Loud refusal, unchanged.</item>
    ///   <item>MORE THAN ONE row carries it. Real BC cannot hold that state at all — its own
    ///   uniqueness trigger is what this PR reproduces — so a backup holding two is genuinely
    ///   inconsistent data, and "adopt one of them" would be a coin toss written into
    ///   <c>UserSecurityId()</c>. Loud refusal, naming the ambiguity.</item>
    /// </list>
    ///
    /// <para>Adoption does not soften any of the BC-faithful refusals in
    /// <c>UserTableTriggerPatches</c>. Those are unchanged: an AL <c>Insert</c> of a duplicate
    /// user name or a duplicate non-empty Windows SID still raises BC's own exception. The only
    /// thing that changed is what the runner's OWN session-user seed does when BC's rule refuses
    /// its row.</para>
    /// </summary>
    private static bool TryAdoptSessionUserSecurityId(
        object session, NCLMetaTable userMeta, string userName, NavGuid generatedSid,
        out string whyNot)
    {
        NavGuid? adopted;
        try
        {
            using var probe = new NavRecord((NavSession)session, UserSystemTableId, SecurityFiltering.Ignored);
            var probeMeta = probe.MetaTable ?? userMeta;
            var nameField = FieldByNameOnUser(probeMeta, UserNameFieldName);
            probe.ALSetRange(nameField.FieldNo, NavValue.CreateNavValueFromObject(nameField, userName));

            if (!probe.ALFindFirstAsync(DataError.TrapError).GetAwaiter().GetResult())
            {
                whyNot = $"no row in the User table carries the name \"{userName}\", so the "
                    + "refusal was not a name collision and there is no row to adopt";
                return false;
            }

            adopted = probe.GetFieldValue(FieldNoByNameOnUser(probeMeta, UserSecurityIdFieldName)) as NavGuid;

            // Read the sid BEFORE stepping the cursor. A second row under one name is a state
            // real BC refuses to hold, so finding one means the data is inconsistent rather than
            // merely surprising — and picking either row would be arbitrary.
            // CS0618: sync-over-async, the same trade the rest of this file makes.
#pragma warning disable CS0618
            var moved = probe.ALNext();
#pragma warning restore CS0618
            if (moved != 0)
            {
                whyNot = $"MORE THAN ONE row in the User table carries the name \"{userName}\". "
                    + "Real BC cannot hold that state — its own uniqueness trigger refuses it — so "
                    + "this data is inconsistent, and adopting one of them would make "
                    + "UserSecurityId() a coin toss";
                return false;
            }
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
            whyNot = $"the User table could not be searched for a row to adopt "
                + $"({inner.GetType().Name}: {inner.Message})";
            return false;
        }

        if (adopted == null || adopted.IsZeroOrEmpty)
        {
            whyNot = $"the row carrying the name \"{userName}\" has no User Security ID, so there "
                + "is no identity on it to adopt";
            return false;
        }

        // Remember what is being overwritten BEFORE overwriting it. The skeleton NavUser is
        // per-process while this seed is per-bundle, so the next bundle in a --watch / --server /
        // multi-bundle run has to start from the generated identity again rather than inheriting
        // this one. Only the FIRST adoption records it; a second in the same process must not
        // overwrite the original with an already-adopted value.
        _generatedSessionUserSid ??= generatedSid;

        if (!TryPokeSessionUserSecurityId(session, adopted))
        {
            whyNot = "the skeleton session's NavUser does not expose the userGuid field this "
                + "runner build writes UserSecurityId() through, so the session could not be "
                + "moved onto the existing row";
            return false;
        }

        // Every User BC creates has a User Property (2000000121) row created with it (#2355).
        // The adopted row got here without passing through UserTableTriggerPatches's insert
        // prepend — it came out of a backup, or out of install code — so that invariant is not
        // guaranteed for it, and the session user is precisely the user for whom
        // UserManagement.DirectSetUserFieldValue does a RAISING Get on that table. Idempotent:
        // TrapError leaves an existing row alone.
        try
        {
            UserTableTriggerPatches.EnsureUserPropertyRow(
                (NavSession)session, adopted, $"session-user adoption of '{userName}'");
        }
        catch (Exception ex)
        {
            // Diagnosis only. The adoption itself has already happened and is sound; a missing
            // companion row is the older #2355 gap resurfacing for a row the runner did not
            // write, and it must not turn into a second exception on top of a completed change.
            var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
            Console.Error.WriteLine(
                $"[warn] UserSystemTable: the adopted User row for '{userName}' has no User Property "
                + $"(2000000121) row and one could not be created ({inner.GetType().Name}: {inner.Message}). "
                + "Microsoft AL reaching NavUserAccountHelper.SetAuthenticationObjectId / "
                + "SetAuthenticationEmail for this user will fail the way AlRunner#2355 describes.");
        }

        // LOUD, and on stderr at [warn] so the default Log component filter cannot eat it.
        // This is the whole mitigation for the one real objection to adopting: a reader must
        // never have to wonder why UserSecurityId() returned what it did.
        Console.Error.WriteLine(
            $"[warn] UserSystemTable: the session user '{userName}' ADOPTED the security id "
            + $"{adopted} from a User row (2000000120) that was already present, instead of the "
            + $"{generatedSid} this runner generated. UserSecurityId() returns the ADOPTED value "
            + "for the rest of this run, so it is a value that came from your data — a --test-data "
            + "backup, or this bundle's own install code — and not one the runner chose. AL that "
            + "asserts a session identity will see a different value here than it would without "
            + "that data. BC refuses two users sharing a name (the runner reproduces that refusal "
            + "in UserTableTriggerPatches), so the alternative was a session whose user is in no "
            + "row at all. See AlRunner#2983 and AlRunner#2296.");

        whyNot = string.Empty;
        return true;
    }

    /// <summary>
    /// Point the skeleton session's identity at <paramref name="adopted"/>. Measured against
    /// BC 28.1's own IL rather than assumed: <c>ALDatabase.ALUserSecurityId()</c> is
    /// <c>NavCurrentThread.Session.User.Id</c>, <c>NavSession.User</c> is
    /// <c>Authenticator.User</c>, <c>NavUserAuthentication.User</c> is its <c>navUser</c> field,
    /// and <c>NavUser.Id</c> is <c>userGuid.Value</c> — with no cached copy anywhere on the
    /// chain. So one field is the whole of the adoption, and this is the same field
    /// <c>BcRuntime</c> pokes when it builds the skeleton user in the first place.
    /// </summary>
    private static bool TryPokeSessionUserSecurityId(object session, NavGuid adopted)
    {
        const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;
        var auth = session.GetType().GetProperty("Authenticator", F)?.GetValue(session);
        var navUser = auth?.GetType().GetField("navUser", F)?.GetValue(auth);
        var fUserGuid = navUser?.GetType().GetField("userGuid", F);
        if (navUser == null || fUserGuid == null) return false;
        AlRunner.Infrastructure.FieldPoke.SetInstance(fUserGuid, navUser, adopted);
        return true;
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
    /// application id) before writing, and since #2983 <c>UserTableTriggerPatches</c> reproduces
    /// that refusal here too.
    ///
    /// <para>Reaching this text at all now means the single-row name collision was NOT the
    /// reason, because that one is adopted rather than refused
    /// (<see cref="TryAdoptSessionUserSecurityId"/>) — so the surviving cases are the ambiguous
    /// one (several rows share the name, which real BC cannot hold) and a refusal from something
    /// other than the name.</para>
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
                    + "validates a unique user name before writing — NOT in a unique index, and the "
                    + "runner reproduces that refusal (see AlRunner/Patches/UserTableTriggerPatches.cs). "
                    + "A single such row is normally ADOPTED rather than refused, so reaching this "
                    + "text means the adoption itself declined — its own reason is appended below";
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
