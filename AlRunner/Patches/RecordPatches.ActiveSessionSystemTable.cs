// RecordPatches.ActiveSessionSystemTable — the runner session's own row in Active Session
// (2000000110), issue #3233.
//
// CLAIM: on a service tier the platform writes this row when the session logs in, keyed by
// (NavEnvironment.GetServiceInstanceId(), NavSession.Id) — to SQL through
// SessionEventTableHandler, or to ActiveSessionDataProvider's in-memory list when
// TrackSessionsInMemory is on. The runner has no login step, so without this seed the table
// answers "no sessions". Every column written here is READ BACK from the same state the AL
// functions answer from (ServiceInstanceId(), SessionId(), UserId(), UserSecurityId()), so the
// row cannot disagree with them; Client Type goes through BC's own TranslateToClientType.
// Citation: corpus "Test Active Session Table" (codeunit 60976, corpus PR #328); BC member
// SessionEventTableHandler.InsertSessionEventRecord.
//
// Left at BC's own field default, not invented: "Server Instance Name", "Client Computer Name"
// and "Database Name" — the runner has no server instance, client machine or database to read
// them from. "Database Name" is #3230.
//
// TRAP: seeded OUTSIDE the dependency-company snapshot window on purpose. The login instant and
// the session unique id belong to this process, and a snapshot restored from the disk cache in
// a later process would otherwise replay the previous process's values. The seed replaces any
// row already stored under the key for the same reason.

using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int ActiveSessionSystemTableId = 2000000110;

    internal enum ActiveSessionSeedOutcome
    {
        /// <summary>This bundle's closure has no Active Session metatable.</summary>
        NoActiveSessionTable,
        /// <summary>The row for (ServiceInstanceId(), SessionId()) was written.</summary>
        Inserted,
        /// <summary>The row could not be written; the reason went to stderr at [warn].</summary>
        NotWritten,
    }

    /// <summary>
    /// Write the reading session's Active Session row. Call after the session-user seed (the
    /// row's "User SID" relates to User, and must be the id an adoption settled on) and before
    /// <c>CaptureInstallBaseline()</c>, so the per-codeunit restore keeps it.
    /// </summary>
    internal static ActiveSessionSeedOutcome EnsureActiveSessionRowSeeded()
    {
        // Warn rather than throw, the UserSystemTable seed's trade: this runs once per app group
        // outside any test, so a throw would fail every test in the bundle, while an AL read of
        // Active Session still fails on its own against the missing row.
        try
        {
            return SeedActiveSessionRowCore();
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
            Console.Error.WriteLine(
                $"[warn] ActiveSessionSystemTable: the session's Active Session row (2000000110) was NOT "
                + $"written — {inner.GetType().Name}: {inner.Message}. Reads of Active Session, and of "
                + "anything projected from it, will answer no row for this session. See AlRunner#3233.");
            return ActiveSessionSeedOutcome.NotWritten;
        }
    }

    private static ActiveSessionSeedOutcome SeedActiveSessionRowCore()
    {
        var meta = EnsureTableInMetadataCache(ActiveSessionSystemTableId);
        if (meta == null) return ActiveSessionSeedOutcome.NoActiveSessionTable;

        var session = AlRunner.BcRuntime.SkeletonSession as NavSession
            ?? throw ActiveSessionShapeGap("there is no skeleton session to read the row back from");

        var (userName, _, userSid) = ReadSkeletonUserIdentity(session);
        if (string.IsNullOrEmpty(userName) || userSid == null)
            throw ActiveSessionShapeGap(
                "the skeleton session exposes no user identity, so \"User ID\" / \"User SID\" have "
                + "no truthful source");

        var loginAt = AlRunner.BcRuntime.SkeletonSessionLoginTime
            ?? throw ActiveSessionShapeGap(
                "no login instant was recorded when the skeleton session was built "
                + "(BcRuntime.SkeletonSessionLoginTime)");

        var instanceId = ALDatabase.ALServiceInstanceID();
        var sessionId = session.Id;

        // Replace, never keep: a row already under this key came from an earlier bundle or a
        // restored snapshot, and its login instant and unique id are not this process's.
        using (var existing = new NavRecord(session, ActiveSessionSystemTableId, SecurityFiltering.Ignored))
        {
#pragma warning disable CS0618
            if (existing.ALGet(DataError.TrapError, NavInteger.Create(instanceId), NavInteger.Create(sessionId)))
                existing.ALDelete(DataError.ThrowError, runApplicationTrigger: false);
#pragma warning restore CS0618
        }

        using var rec = new NavRecord(session, ActiveSessionSystemTableId, SecurityFiltering.Ignored);
        var m = rec.MetaTable ?? meta;

        void Set(string name, object value)
        {
            var f = FieldByNameOn(m, name);
            rec.SetFieldValue(f.FieldNo, NavValue.CreateNavValueFromObject(f, value));
        }

        Set("Server Instance ID", instanceId);
        Set("Session ID", sessionId);
        rec.SetFieldValue(FieldByNameOn(m, "User SID").FieldNo, userSid);
        Set("User ID", userName!);
        // BC's own mapping (SessionEventTableHandler.TranslateToClientType) over the session's
        // ClientConnectionType. That property is never set on the skeleton and the mapping has no
        // entry for it, so this answers Unknown: a constant, pinned by the ActiveSessionTable fixture.
        Set("Client Type", TranslateSkeletonClientType(session));
        // BC stores DateTime.UtcNow; SkeletonSessionLoginTime is the same instant in host-local
        // time (see its doc), so Session's GetDatePart/GetTimePart view of this value matches the
        // Session table's Login Date / Login Time.
        Set("Login Datetime", DateTime.SpecifyKind(loginAt, DateTimeKind.Local).ToUniversalTime());
        Set("Server Computer Name", ReadHostName());
        Set("Session Unique ID", EnsureSkeletonUniqueSessionId(session));

#pragma warning disable CS0618
        rec.ALInsert(DataError.ThrowError, runApplicationTrigger: false, insertWithSystemId: false);
#pragma warning restore CS0618
        return ActiveSessionSeedOutcome.Inserted;
    }

    /// <summary>
    /// The skeleton session's UniqueSessionId, generated once if the uninitialized skeleton left
    /// it empty. Populating skeleton state, not a fake: on a tier the id is minted when the
    /// session is created, and every later reader (SessionEventTableHandler, this row) reads it
    /// back from the session.
    /// </summary>
    private static Guid EnsureSkeletonUniqueSessionId(NavSession session)
    {
        if (session.UniqueSessionId != Guid.Empty) return session.UniqueSessionId;
        var backing = typeof(NavSession).GetField("<UniqueSessionId>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw ActiveSessionShapeGap(
                "NavSession.UniqueSessionId is empty and has no backing field to populate");
        backing.SetValue(session, Guid.NewGuid());
        return session.UniqueSessionId;
    }

    private static int TranslateSkeletonClientType(NavSession session)
    {
        var translate = typeof(NavSession).Assembly
            .GetType("Microsoft.Dynamics.Nav.Runtime.SessionEventTableHandler")
            ?.GetMethod("TranslateToClientType", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw ActiveSessionShapeGap(
                "BC's SessionEventTableHandler.TranslateToClientType was not found, so \"Client Type\" "
                + "would have to be mapped by hand");
        return Convert.ToInt32(translate.Invoke(null, new object[] { session.ClientConnectionType }));
    }

    private static NCLMetaField FieldByNameOn(NCLMetaTable table, string fieldName)
    {
        foreach (var f in GetAllFields(table) ?? Enumerable.Empty<NCLMetaField>())
            if (string.Equals(f.FieldName, fieldName, StringComparison.OrdinalIgnoreCase))
                return f;
        throw ActiveSessionShapeGap($"the table states no \"{fieldName}\" field");
    }

    internal static AlRunner.Infrastructure.RunnerOutOfScopeException ActiveSessionShapeGap(string detail)
        => VirtualTableShapeGap("Active Session (system table 2000000110)", "active-session-row", detail,
            "docs/limitations.md#virtual-table-shape-gaps");
}
