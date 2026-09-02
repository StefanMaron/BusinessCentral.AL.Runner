// UserTableTriggerPatches — the platform-level companion row BC creates when a User is
// inserted.
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
// WHAT THIS DELIBERATELY DOES NOT DO
//   BC's `case 2000000120:` arm also validates (unique user name, unique Windows SID,
//   authentication email, application id) and its OnAfterDeleteAsync arm cascades a User
//   delete into Access Control (2000000053), User Property (2000000121), User
//   Personalization (2000000107) and 2000000233. None of that is reproduced here; the
//   delete cascade is tracked separately in #2356. This file establishes exactly one
//   invariant — a non-temporary User row has a User Property row — and says so rather than
//   quietly implementing a third of a trigger.
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

namespace AlRunner.Patches;

public static class UserTableTriggerPatches
{
    /// <summary>The User system table.</summary>
    internal const int UserTableId = 2000000120;

    /// <summary>The User Property system table — one row per User, created with it.</summary>
    internal const int UserPropertyTableId = 2000000121;

    // Field numbers are resolved off the metatables' OWN field names rather than hardcoded,
    // so a BC version that renumbers either table is followed instead of silently misread.
    // BC's own trigger hardcodes 1 and 10; the names those correspond to today are
    // "User Security ID" and "Telemetry User ID".
    private const string UserSecurityIdFieldName = "User Security ID";
    private const string TelemetryUserIdFieldName = "Telemetry User ID";

    /// <summary>
    /// Prepended to NavRecord.ALInsertAsync(DataError, bool, bool). A no-op for every table
    /// but User (2000000120); for that one it creates the matching User Property row the way
    /// BC's SystemTableTriggers.OnBeforeInsertAsync does.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void CreateUserPropertyOnUserInsert(object? record)
    {
        if (record is not NavRecord { IsTemporary: false } user) return;
        if (user.MetaTable?.TableId != UserTableId) return;

        var sid = user.GetFieldValue(FieldNoByName(user.MetaTable, UserSecurityIdFieldName));
        // A User row with no security id is not one BC would have accepted either — its own
        // trigger throws NavNCLUserTableInvalidUserSidException before reaching the companion
        // insert. Leave that refusal to BC rather than inventing a property row keyed on the
        // null GUID.
        if (sid == null || sid.IsZeroOrEmpty) return;

        var session = user.ParentSession
            ?? throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                "User (2000000120) insert",
                "user-property-companion-row — the User record under insert has no session, so the "
                + "User Property row BC creates alongside it cannot be written; see docs/scope.md");

        using var property = new NavRecord(session, UserPropertyTableId, SecurityFiltering.Ignored);
        var propertyMeta = property.MetaTable
            ?? throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                "User Property (2000000121)",
                "user-property-companion-row — the User Property table has no metadata in this run, "
                + "so the row BC creates alongside every User cannot be written; see docs/scope.md");

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
    /// The field number <paramref name="fieldName"/> carries on <paramref name="table"/>.
    /// Throws rather than returning a sentinel: a missing field means the companion row
    /// would be written with the wrong shape, which is the silent-wrong-answer case
    /// .claude/rules/loud-failures.md forbids.
    /// </summary>
    private static int FieldNoByName(NCLMetaTable table, string fieldName)
    {
        foreach (var f in RecordPatches.GetAllFields(table) ?? Enumerable.Empty<NCLMetaField>())
            if (string.Equals(f.FieldName, fieldName, StringComparison.OrdinalIgnoreCase))
                return f.FieldNo;
        throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
            $"{table.TableName} ({table.TableId})",
            $"user-property-companion-row — the table states no \"{fieldName}\" field, so the User "
            + "Property row BC creates alongside every User cannot be written; see docs/scope.md");
    }
}
