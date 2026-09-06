// UserTableTriggerPatches — the runner's stand-in for BC's SystemTableTriggers arms on the
// User system table (2000000120): the uniqueness validation and companion row its insert arm
// runs, and the cascade its delete arm runs.
//
// WHY THIS EXISTS
//   On a real tier, inserting into User (2000000120) does more than write that one row.
//   Ncl's SystemTableTriggers.OnBeforeInsertAsync has a `case 2000000120:` arm that, after
//   its validation, does exactly this:
//
//       NavRecord navRecord = NavGlobal.NCLMetadata
//           .GetMetaTableById(2000000121, requireCompiled: true)
//           .CreateObjectInstance(session, isTemporary: false, null, string.Empty,
//                                 SecurityFiltering.Ignored);
//       navRecord.SetFieldValue(1,  userSid);            // "User Security ID"
//       navRecord.SetFieldValue(10, NavGuid.NewGuid());  // "Telemetry User ID"
//       await navRecord.InsertAsync((DataError)0, runApplicationTrigger: false,
//                                   runGlobalTrigger: false);
//
//   So every User has a matching User Property (2000000121) row from the moment it exists,
//   and BC's own code relies on that. UserManagement.DirectSetUserFieldValue — reached from
//   AL through NavUserAccountHelper.SetAuthenticationObjectId / SetAuthenticationEmail /
//   SetDirectoryRoleIdList, which is how Microsoft's own test libraries create users — does
//
//       navRecord.Get((DataError)1, new NavRecordId(2000000121, [ new NavGuid(sid) ]));
//
//   with the RAISING error level. The runner bypasses BC's trigger dispatch on insert (see
//   RecordWritePatches.NavRecord_InsertAsync), so that row was never created and every such
//   call died with:
//
//       NavCSideRecordNotFoundException: The User Property does not exist.
//       Identification fields and values: User Security ID='{...}'
//
//   Issue #2355. Measured on Microsoft's Tests-SINGLESERVER bucket, BC 28.1.49838.53910.
//
// WHY A PREPEND, AND WHY THIS ENTRY POINT
//   NavRecord.ALInsertAsync(DataError, bool, bool) is the single async entry point every AL
//   `Insert()` surface funnels through — the same one AssignAutoIncrement and
//   StampSystemFieldsOnInsert are already prepended to. Running BEFORE the user row is
//   written matches BC, whose own companion insert happens in OnBEFOREInsert.
//
//   The companion insert itself goes through ALInsert, which re-enters this prepend with
//   table 2000000121 and returns immediately — the table check below is what bounds it.
//
// WHAT ELSE THE SAME ARM DOES, AND WHAT IS HERE NOW
//   BC's `case 2000000120:` insert arm validates before it writes:
//
//       if (!(await IsUserFieldUniqueAsync(recordBuffer, 2, insert: true)))
//           throw NavNCLUserTableUserNameMustBeUniqueException.Create();
//       await ValidateAuthenticationEmailAsync(recordBuffer, insert: true);
//       await ValidateApplicationIdAsync(recordBuffer, insert: true);
//       ... field 7 non-empty && !unique -> NavNCLUserTableUserWindowsSidMustBeUniqueException
//
//   and its OnAfterDeleteAsync `case 2000000120:` arm cascades:
//
//       await DeleteAllFromTableAsync(session, 2000000053, 1, userSid);
//       await DeleteAllFromTableWithMaximizedPermissionAsync(session, 2000000121, 1, userSid);
//       await DeleteAllFromTableWithMaximizedPermissionAsync(session, 2000000107, 4, userSid);
//       await DeleteAllFromTableWithMaximizedPermissionAsync(session, 2000000233, 5, userSid);
//       await NavSqlRecentRecords.DeleteAllForUser(session, userSid.ToGuid());
//       session.Tenant.AuthenticationCache.ExpireUser(userSid.ToGuid());
//
//   REPRODUCED HERE NOW: the two uniqueness refusals (#2983) and the four table cascades
//   (#2356). Nothing about the shape changed — this is still a prepend on NavRecord's own AL
//   entry points, using BC's own exception types, and it is still a no-op for every table but
//   User.
//
//   WHICH TABLES THOSE FOUR IDS ACTUALLY ARE, read off the platform's own System.app rather
//   than from the names in #2356, which got two of them wrong:
//
//       2000000053  Access Control                   field 1 "User Security ID"  (Guid)
//       2000000121  User Property                    field 1 "User Security ID"  (Guid)
//       2000000107  Isolated Storage                 field 4 "User Id"           (Guid)
//       2000000233  Tenant Report Layout Selection   field 5 "User ID"           (Guid)
//
//   NOT reproduced, and deliberately so:
//     * ValidateAuthenticationEmailAsync / ValidateApplicationIdAsync. Both are format-and-
//       uniqueness validation whose *format* half is BC's own rule set; reproducing it would
//       be re-implementing behaviour rather than reusing it, and the authentication-email
//       surface is #2363's subject and assigned. The two refusals implemented here need no
//       rule set at all — they are existence lookups on a field the table already states.
//     * NavSqlRecentRecords.DeleteAllForUser (a SQL-side table the runner has no store for)
//       and AuthenticationCache.ExpireUser (a tenant cache the skeleton session does not
//       have). Neither is observable from AL in this runner.
//
// PRECOMPILED-DLL RESPECT
//   No AL business-logic body is touched. This is a static helper Cecil PREPENDS to
//   NavRecord's own AL insert entry point in the runtime engine (Ncl.dll), the same
//   mechanism AssignAutoIncrement, the rowversion clock and the All Profile write guards
//   already use. Every type it touches (NavRecord, NCLMetaField, NavGuid, DataError) is
//   runtime-engine, never AL business logic.
using System.Runtime.CompilerServices;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Microsoft.Dynamics.Nav.Types.Exceptions;

namespace AlRunner.Patches;

public static class UserTableTriggerPatches
{
    /// <summary>The User system table.</summary>
    internal const int UserTableId = 2000000120;

    /// <summary>The User Property system table — one row per User, created with it.</summary>
    internal const int UserPropertyTableId = 2000000121;

    /// <summary>Access Control (2000000053), BC's first cascade target on a User delete.</summary>
    internal const int AccessControlTableId = 2000000053;

    /// <summary>Isolated Storage (2000000107) — BC's `2000000107, 4` cascade target.</summary>
    internal const int IsolatedStorageTableId = 2000000107;

    /// <summary>Tenant Report Layout Selection (2000000233) — BC's `2000000233, 5` target.</summary>
    internal const int TenantReportLayoutSelectionTableId = 2000000233;

    // Field numbers are resolved off the metatables' OWN field names rather than hardcoded,
    // so a BC version that renumbers either table is followed instead of silently misread.
    // BC's own trigger hardcodes the numbers; the names those correspond to today are below,
    // read off the platform's System.app table sources rather than guessed.
    private const string UserSecurityIdFieldName = "User Security ID";   // User 1, AC 1, UP 1
    private const string TelemetryUserIdFieldName = "Telemetry User ID"; // User Property 10
    private const string UserNameFieldName = "User Name";                // User 2
    private const string WindowsSecurityIdFieldName = "Windows Security ID"; // User 7
    private const string IsolatedStorageUserIdFieldName = "User Id";     // Isolated Storage 4
    private const string TenantReportLayoutUserIdFieldName = "User ID";  // Tenant Rep. Layout 5

    /// <summary>
    /// Prepended to NavRecord.ALInsertAsync(DataError, bool, bool). A no-op for every table
    /// but User (2000000120); for that one it runs BC's
    /// SystemTableTriggers.OnBeforeInsertAsync `case 2000000120:` arm in BC's own order —
    /// the uniqueness refusals first, then the User Property companion row.
    ///
    /// <para>The refusals THROW rather than returning false, which is what BC does and is not
    /// what <c>DataError.TrapError</c> converts. TrapError's contract covers data errors (a
    /// key violation, a missing row), not an error raised by a system-table trigger — so on a
    /// real tier <c>if not User.Insert() then</c> over a duplicate user name raises rather
    /// than taking the false branch, and it raises here too.</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void OnBeforeUserInsert(object? record)
    {
        if (record is not NavRecord { IsTemporary: false } user) return;
        if (user.MetaTable?.TableId != UserTableId) return;

        // #2983 — BC validates before it writes, and before the companion insert below.
        // Order matches OnBeforeInsertAsync's own: user name (field 2) first, Windows SID
        // (field 7) after. A run that stops at the first refusal therefore stops on the same
        // one BC would.
        //
        // ...EXCEPT when a row with this row's OWN primary key is already in the table. On a
        // real tier that state cannot arise at this point — the row being inserted is not in
        // the table yet, which is exactly why BC passes `insert: true` and uses EqualsFilter
        // rather than its UserTableFilter (the modify path, which excludes the row's own
        // security id). In the runner it CAN arise, because the session-user seed re-inserts
        // an identity that a resumed app group, a bundle's install code or a --test-data
        // backup may already have written. Validating there would find the row itself and
        // refuse it as a name collision with a user that IS this user, turning
        // RecordPatches.EnsureUserSystemTableRowSeeded's benign AlreadyPresent outcome into a
        // loud Refused over a row that is present — the opposite of that branch's stated
        // invariant. The insert is refused either way; letting it fall through means it is
        // refused by the primary key, which is what it actually collides with.
        if (!RowWithSameSecurityIdExists(user))
        {
            ValidateUserFieldIsUnique(user, UserNameFieldName, skipWhenEmpty: false);
            ValidateUserFieldIsUnique(user, WindowsSecurityIdFieldName, skipWhenEmpty: true);
        }

        var sid = user.GetFieldValue(FieldNoByName(user.MetaTable, UserSecurityIdFieldName));
        // A User row with no security id is not one BC would have accepted either — its own
        // trigger throws NavNCLUserTableInvalidUserSidException before reaching the companion
        // insert. Leave that refusal to BC rather than inventing a property row keyed on the
        // null GUID.
        if (sid == null || sid.IsZeroOrEmpty) return;

        var session = user.ParentSession
            ?? throw RunnerShapeGap.UserPropertyCompanionRow(
                "User (2000000120) insert",
                "the User record under insert has no session, so the User Property row BC creates "
                + "alongside it cannot be written");

        using var property = new NavRecord(session, UserPropertyTableId, SecurityFiltering.Ignored);
        var propertyMeta = property.MetaTable
            ?? throw RunnerShapeGap.UserPropertyCompanionRow(
                "User Property (2000000121)",
                "the User Property table has no metadata in this run, so the row BC creates alongside "
                + "every User cannot be written");

        property.SetFieldValue(FieldNoByName(propertyMeta, UserSecurityIdFieldName), sid);
        property.SetFieldValue(FieldNoByName(propertyMeta, TelemetryUserIdFieldName), NavGuid.NewGuid());

        // TrapError, exactly as BC's own trigger uses: a User Property row that already
        // exists for this SID is left alone rather than turned into a duplicate-key error.
        // runApplicationTrigger: false / insertWithSystemId: false mirror BC's call.
        // CS0618: BC marks the synchronous ALInsert obsolete in favour of the async form
        // because of the sync-over-async cost. A Cecil prepend is a `void` method with no
        // await point available to it, and the runner executes AL on one thread anyway, so
        // the synchronous form is the only one callable here — the same trade the other
        // prepends in this codebase make.
#pragma warning disable CS0618
        property.ALInsert(DataError.TrapError, runApplicationTrigger: false, insertWithSystemId: false);
#pragma warning restore CS0618
    }

    /// <summary>
    /// Is a User row carrying this row's own "User Security ID" already in the table? See the
    /// long comment at the call site: this is the one state BC's insert arm cannot be in and
    /// the runner's session-user seed routinely is.
    /// </summary>
    private static bool RowWithSameSecurityIdExists(NavRecord user)
    {
        var meta = user.MetaTable;
        if (meta == null) return false;
        var sid = user.GetFieldValue(FieldNoByName(meta, UserSecurityIdFieldName));
        if (sid == null || sid.IsZeroOrEmpty) return false;

        var session = user.ParentSession;
        if (session == null) return false;

        using var probe = new NavRecord(session, UserTableId, SecurityFiltering.Ignored);
        // CS0618: sync-over-async, the same trade the rest of this file makes.
#pragma warning disable CS0618
        return probe.ALGet(DataError.TrapError, sid);
#pragma warning restore CS0618
    }

    /// <summary>
    /// BC's <c>IsUserFieldUniqueAsync(recordBuffer, fieldNo, insert: true)</c> for one field of
    /// the User row under insert, raising BC's OWN exception type when it is already taken.
    ///
    /// <para>The lookup is the same shape BC's is: an equals-filter on that one field over the
    /// User table, first row only. The row under insert is not in the table yet, so nothing has
    /// to be excluded from the filter — which is exactly why BC passes <c>insert: true</c> and
    /// uses <c>EqualsFilter</c> rather than its <c>UserTableFilter</c> (that one is the modify
    /// path, and excludes the row's own security id).</para>
    ///
    /// <para>Case sensitivity is whatever BC's own filter machinery does with this field's type
    /// and collation — deliberately not re-decided here. #2983 lists it as an open question, and
    /// answering it by hand-writing a comparison would be answering it wrongly.</para>
    /// </summary>
    private static void ValidateUserFieldIsUnique(NavRecord user, string fieldName, bool skipWhenEmpty)
    {
        var meta = user.MetaTable;
        if (meta == null) return;

        var field = FieldByName(meta, fieldName);
        var value = user.GetFieldValue(field.FieldNo);
        if (value == null) return;
        // BC skips the Windows SID check when the field is empty (`navText2 != null &&
        // !navText2.IsZeroOrEmpty`) and does NOT skip the user-name one. Two users with no
        // Windows SID are ordinary; two users with the same name are what BC refuses.
        if (skipWhenEmpty && value.IsZeroOrEmpty) return;

        var session = user.ParentSession;
        if (session == null) return;

        bool taken;
        using (var probe = new NavRecord(session, UserTableId, SecurityFiltering.Ignored))
        {
            probe.ALSetRange(field.FieldNo, value);
            taken = probe.ALFindFirstAsync(DataError.TrapError).GetAwaiter().GetResult();
        }
        if (!taken) return;

        // BC's own exception types, constructed through BC's own factories, so the message AL
        // sees is BC's message ("The user name must be unique.") rather than a runner paraphrase.
        throw fieldName == WindowsSecurityIdFieldName
            ? NavNCLUserTableUserWindowsSidMustBeUniqueException.Create()
            : NavNCLUserTableUserNameMustBeUniqueException.Create();
    }

    /// <summary>
    /// Prepended to NavRecord.ALDeleteAsync(DataError, bool, bool). A no-op for every table but
    /// User (2000000120); for that one it runs the four table cascades of BC's
    /// SystemTableTriggers.OnAfterDeleteAsync `case 2000000120:` arm (#2356).
    ///
    /// <para>WHY THIS ONE ENTRY POINT IS ENOUGH, INCLUDING FOR <c>DeleteAll()</c>. #2356 predicted
    /// that a prepend here would miss <c>User.DeleteAll()</c>, because AL binds that to
    /// <c>ALDeleteAll(bool)</c> → <c>DeleteAllAsync(bool)</c> rather than to
    /// <c>ALDeleteAsync</c>. Read against BC's shipped IL that is not what happens for this
    /// table: <c>DeleteAllAsync</c> takes its bulk path only when
    /// <c>CanUseBulkDeleteAll(runApplicationTrigger, this)</c> holds, and that predicate ends in
    /// <c>!SystemTableTriggers.TableHasSystemDeleteTrigger(record)</c> — whose body is a static
    /// switch listing 2000000120. So for User it is always false, and <c>DeleteAllAsync</c>
    /// falls to its row loop, which calls <c>record.ALDeleteAsync(DataError.ThrowError,
    /// runApplicationTrigger, isBulkDelete: true)</c> per row. Both AL surfaces funnel here.
    /// The <c>SurdDeleteAll…</c> test in tests/runner-extras/user-delete-cascade measures it
    /// rather than trusting the reading.</para>
    ///
    /// <para>WHY IT GUARDS ON THE ROW EXISTING. BC cascades in OnAFTERDelete — i.e. only for a
    /// delete that happened. A Cecil prepend runs before, so without the guard a refused or
    /// no-op delete would still take the dependent rows with it.</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void OnAfterUserDelete(object? record)
    {
        if (record is not NavRecord { IsTemporary: false } user) return;
        if (user.MetaTable?.TableId != UserTableId) return;

        var sid = user.GetFieldValue(FieldNoByName(user.MetaTable, UserSecurityIdFieldName));
        // Same reasoning as the insert side: a User row with no security id is not one BC
        // would hold, and there is nothing for the cascade to key on.
        if (sid == null || sid.IsZeroOrEmpty) return;

        var session = user.ParentSession;
        if (session == null) return;

        using (var probe = new NavRecord(session, UserTableId, SecurityFiltering.Ignored))
        {
            // CS0618: sync-over-async, the same trade the rest of this file makes — a Cecil
            // prepend is a void method with no await point available to it.
#pragma warning disable CS0618
            if (!probe.ALGet(DataError.TrapError, sid)) return;
#pragma warning restore CS0618
        }

        CascadeDeleteForUser(session, AccessControlTableId, UserSecurityIdFieldName, sid);
        CascadeDeleteForUser(session, UserPropertyTableId, UserSecurityIdFieldName, sid);
        CascadeDeleteForUser(session, IsolatedStorageTableId, IsolatedStorageUserIdFieldName, sid);
        CascadeDeleteForUser(session, TenantReportLayoutSelectionTableId, TenantReportLayoutUserIdFieldName, sid);
    }

    /// <summary>
    /// Delete every row of <paramref name="tableId"/> whose <paramref name="fieldName"/> is this
    /// user's security id — BC's <c>DeleteAllFromTableAsync(session, tableId, fieldNo, userSid)</c>.
    ///
    /// <para>A table absent from this bundle's closure is skipped rather than refused. That is
    /// not a silent no-op in the sense loud-failures.md forbids: a table with no metadata in
    /// this run has no storage and therefore no rows to orphan, so there is no wrong answer to
    /// hide. A table that IS present but does not state the field is a different thing entirely
    /// and still throws, through <see cref="FieldNoByName"/>.</para>
    /// </summary>
    private static void CascadeDeleteForUser(NavSession session, int tableId, string fieldName, NavValue userSid)
    {
        var meta = RecordPatches.EnsureTableInMetadataCache(tableId);
        if (meta == null) return;

        using var rows = new NavRecord(session, tableId, SecurityFiltering.Ignored);
        var rowMeta = rows.MetaTable ?? meta;
        rows.ALSetRange(FieldNoByName(rowMeta, fieldName), userSid);
        // runTrigger: false mirrors BC, whose cascade is platform-level and runs no AL triggers.
        // CS0618: sync-over-async, as above.
#pragma warning disable CS0618
        rows.ALDeleteAll(runTrigger: false);
#pragma warning restore CS0618
    }

    /// <summary>
    /// The metafield <paramref name="fieldName"/> names on <paramref name="table"/>. Throws for
    /// the same reason <see cref="FieldNoByName"/> does.
    /// </summary>
    private static NCLMetaField FieldByName(NCLMetaTable table, string fieldName)
    {
        foreach (var f in RecordPatches.GetAllFields(table) ?? Enumerable.Empty<NCLMetaField>())
            if (string.Equals(f.FieldName, fieldName, StringComparison.OrdinalIgnoreCase))
                return f;
        throw RunnerShapeGap.UserPropertyCompanionRow(
            $"{table.TableName} ({table.TableId})",
            $"the table states no \"{fieldName}\" field, so the User system-table trigger arm BC "
            + "runs for this table cannot be reproduced");
    }

    /// <summary>
    /// The field number <paramref name="fieldName"/> carries on <paramref name="table"/>.
    /// Throws rather than returning a sentinel: a missing field means the companion row
    /// would be written with the wrong shape, which is the silent-wrong-answer case
    /// .claude/rules/loud-failures.md forbids.
    /// </summary>
    private static int FieldNoByName(NCLMetaTable table, string fieldName)
        => FieldByName(table, fieldName).FieldNo;
}
