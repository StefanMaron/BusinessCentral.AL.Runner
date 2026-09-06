// RecordPatches.SessionVirtualTable — managed provider for the Session (2000000009) system
// virtual table.
//
// WHY THIS EXISTS (issue #2940)
//   The runner's identity lived in SESSION STATE and not in the table AL reads. CompanyName(),
//   UserId(), UserSecurityId() and SessionId() all answered correctly out of the skeleton
//   NavSession, while `Record Session` answered ZERO ROWS — nothing populated it and nothing
//   refused it, so every read silently reported "nobody is logged on". Company (2000000006)
//   and User (2000000120) had already been seeded for exactly this reason (#2329, #2296);
//   Session is the same shape, one layer over.
//
//   That is a wrong answer, not a missing feature: FindSet() is false, Count() is 0, and no
//   AL caller can tell that apart from an idle server.
//
// WHAT REAL BC ANSWERS — MEASURED, NOT ASSUMED
//   Microsoft.Dynamics.Nav.Runtime.SessionDataProvider (Ncl.dll, BC 28.1.49838.53910)
//   declares TableId => 2000000009, and its whole GetAllItems body is:
//
//       NavSession navSession = NavCurrentThread.Session;
//       using NavRecord navRecord = new NavRecord(navSession, 2000000110);   // Active Session
//       navRecord.ALGet(NavInteger.Create(NavEnvironment.Instance.GetServiceInstanceId()),
//                       NavInteger.Create(navSession.Id));
//       NavDateTime navDateTime = (NavDateTime)navRecord.GetFieldValue(9);   // Login Datetime
//       buffer[0] = NavInteger.Create(navSession.Id);                        // Connection ID
//       buffer[1] = navRecord.GetFieldValue(6);                              // User ID
//       buffer[2] = NavBoolean.True;                                         // My Session
//       buffer[3] = navDateTime.GetTimePart(navSession);                     // Login Time
//       buffer[4] = navDateTime.GetDatePart(navSession);                     // Login Date
//       buffer[5] = navRecord.GetFieldValue(10);                             // Database Name
//       buffer[6] = NavText.Create(navRecord.GetFieldValue(7).ToString());   // Application Name
//       buffer[7] = GetOptionValue(18, (int)navSession.Authenticator.AuthenticationMethod);
//       buffer[8] = NavText.Create(DnsHelper.HostName);                      // Host Name
//       return new ReadOnlyRecordBuffer[1] { CreateVirtualRecordBuffer(...) };
//
//   Three things in that body are load-bearing here, and none of them is what the issue
//   assumed:
//
//   1. **It returns exactly ONE row — the reading session.** Not every logged-on session.
//      `new ReadOnlyRecordBuffer[1]`, unconditionally, with `My Session` a constant TRUE. So
//      "who is logged on right now" is NOT what this table answers on a modern tier; it
//      answers "who am I". That is a claim about BC rather than about the runner, so it is
//      adjudicated upstream — see "Which claims are adjudicated where" below — and this
//      provider matches it rather than inventing extra rows.
//   2. Every identity column is READ BACK from the session (`navSession.Id`) or from the
//      session's own Active Session row, never recomputed. This provider follows that rule
//      literally: the columns come from the same skeleton state SessionId() and UserId()
//      answer from, so the table and the session surfaces cannot disagree.
//   3. Four columns come from Active Session (2000000110), a tenant-database table the runner
//      does not maintain. Two of them the runner can answer from state it genuinely holds
//      (the user, and the login instant); two it cannot — see the next section.
//
// THE COLUMNS
//   Answered:
//     Connection ID  ← the skeleton session's Id, the same value AL's SessionId() returns.
//                      That is 0 in the runner (GetUninitializedObject skips NavSession's
//                      `Id = -1` field initializer), and 0 is the honest read-back: writing
//                      any other number here would make the table disagree with SessionId().
//     User ID        ← the skeleton user name, the same value UserId() returns. BC reads it
//                      from Active Session field 6, which is the same fact one table over.
//     My Session     ← true, BC's own unconditional constant for the single row.
//     Login Date     ← BcRuntime.SkeletonSessionLoginTime, split the way BC splits Active
//     Login Time       Session."Login Datetime" through the session's time zone. The skeleton
//                      session's ClientTimeZone is TimeZoneInfo.Local, so the host-local
//                      instant recorded at session construction IS that view.
//     Login Type     ← BC's own expression, reproduced: the direct (int) cast of
//                      session.Authenticator.AuthenticationMethod into field 18's option.
//                      Guarded — see BuildSessionValue.
//     Host Name      ← the machine hosting the session, which is what BC's DnsHelper.HostName
//                      reports on a tier. Host-derived, exactly like the Time Zone provider's
//                      ids, so the VALUE is a property of the machine and no test may assert
//                      a specific one.
//
//   Left at BC's own per-field default (NCLMetaField.EmptyValue), NOT invented:
//     Database Name     — BC reads Active Session field 10. The runner has no database, so
//                         there is no name to read back.
//     Application Name  — BC reads Active Session field 7 (the client type) and stringifies
//                         it. Which client type a runner session is, and what a tier's
//                         .ToString() of that option renders as, are both unmeasured here.
//   Tracked as #3230 rather than guessed. This follows the Published Application seed's
//   precedent (RecordPatches.PublishedApplicationSystemTable.cs): columns with no truthful
//   source keep BC's own default, and the unknown is recorded as an issue instead of being
//   written into a row where nothing can tell it apart from a measurement.
//
// WHICH CLAIMS ARE ADJUDICATED WHERE
//   What real BC answers for this table is plain BC behaviour, so it is asserted upstream in
//   StefanMaron/BusinessCentral.AL.Language.Tests against a live service tier
//   (.claude/rules/bc-behavior-tests-go-upstream.md). Session is Scope = Cloud and is NOT in
//   Microsoft.Dynamics.Nav.Types.SystemTables.internalTables, so the Cloud coverage app can
//   name it and the eight `BC <ver> / test` legs adjudicate — the reachability wall that
//   sank corpus PR #153 for table 2000000071 does not apply.
//
//   AlRunner.Tests/SessionVirtualTableTests.cs pins the RUNNER-MECHANISM half: that the table
//   is populated at all, and that every identity column is read back from the session rather
//   than fabricated.
//
// PRECOMPILED-DLL RESPECT
//   Runtime-engine types only (NCLMetaTable, NCLMetaField, NavValue, ReadOnlyRecordBuffer,
//   TempTableDataProvider), reached through the same helpers every sibling provider in this
//   directory resolves. No AL business-logic body is touched, and BC's own
//   GetSystemPopulatedVirtualRecordValues / GetDefaultNavValue fill everything this file does
//   not answer.

using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int SessionVirtualTableId = 2000000009;

    /// <summary>
    /// Every refusal in this file, built in one place. See
    /// RecordPatches.VirtualTableShapeGap.cs for the three-bucket classification and for why
    /// the anchor is "not-yet-implemented" rather than a docs/scope.md section (#2945).
    /// </summary>
    /// <remarks>
    /// Category (2) throughout: real BC answers this table on every service tier, so a refusal
    /// here is always the runner failing to keep up, never BC's answer. Answering with no rows
    /// instead is the exact defect #2940 is about — an empty Session table reads as "nobody is
    /// logged on", which no AL caller can tell apart from a real answer.
    /// </remarks>
    internal static RunnerOutOfScopeException SessionVirtualShapeGap(string detail)
        => VirtualTableShapeGap("Session (virtual table 2000000009)", "session-virtual-table", detail,
            "docs/limitations.md#virtual-table-shape-gaps");

    // One-shot per provider, like Time Zone's: the row set is a single row describing a session
    // whose identity cannot change during a run, so there is nothing to top up later.
    private static readonly ConditionalWeakTable<object, object> _sessionPopulatedProviders = new();

    private static bool IsSessionVirtualTable(NCLMetaTable? table)
        => table != null && table.TableId == SessionVirtualTableId;

    /// <summary>
    /// The runner's own session as the Session virtual table exposes it. Every member is read
    /// back from the skeleton NavSession; nothing here is computed a second time.
    /// </summary>
    private sealed record SessionRow(
        int ConnectionId, string UserId, DateTime LoginAt, int LoginTypeOrdinal, string HostName);

    /// <summary>
    /// Populate the in-memory store behind Session (2000000009) with the one row BC's own
    /// SessionDataProvider produces: the reading session, flagged My Session.
    /// </summary>
    private static void PopulateSessionVirtualTable(object dataAccess, NCLMetaTable metaTable, object session)
    {
        EnsureAllObjReflection(metaTable);
        // Binds NavBoolean.Create(bool), which "My Session" needs. Named for the table it was
        // first written for; it resolves nothing report-specific, and binding it twice is a
        // no-op. Called here rather than duplicated so there is one place NavBoolean is
        // resolved by name across BC versions.
        EnsureReportMetadataReflection(metaTable);
        EnsureDataAccessProviderReflection(dataAccess);

        var provider = _pDataAccessDataProvider!.GetValue(dataAccess)
            ?? throw SessionVirtualShapeGap("data access has no in-memory provider");

        if (_sessionPopulatedProviders.TryGetValue(provider, out _)) return;

        var row = ReadSkeletonSessionRow(session);
        InsertVirtualRow(provider, metaTable,
            new object[] { SessionVirtualTableId, row.ConnectionId, 0, 0 },
            field => BuildSessionValue(field, row));

        _sessionPopulatedProviders.Add(provider, new object());
    }

    /// <summary>
    /// Read the runner's own session identity back out of the skeleton NavSession — the same
    /// state SessionId() and UserId() answer from, walked through the same reflection chain
    /// <see cref="ReadSkeletonUserIdentity"/> uses.
    /// </summary>
    /// <remarks>
    /// The user name is REQUIRED, not defaulted: a Session row carrying a blank "User ID" is
    /// the silent wrong answer this whole file exists to remove, and it would be indisputably
    /// worse than the empty table it replaces, because FindSet() would then succeed.
    /// </remarks>
    private static SessionRow ReadSkeletonSessionRow(object session)
    {
        const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;

        var idProp = session.GetType().GetProperty("Id", F)
            ?? throw SessionVirtualShapeGap(
                "NavSession has no Id property, so there is no connection id to read back — "
                + "the column would have to be invented, and it would then disagree with "
                + "whatever AL's SessionId() reports");
        var connectionId = idProp.GetValue(session) as int?
            ?? throw SessionVirtualShapeGap("NavSession.Id did not read as an Int32");

        var (userName, _, _) = ReadSkeletonUserIdentity(session);
        if (string.IsNullOrEmpty(userName))
            throw SessionVirtualShapeGap(
                "the skeleton session exposes no user name, so Session.\"User ID\" has no "
                + "truthful source; a blank row here would read as a logged-on session with "
                + "no user");

        var loginAt = AlRunner.BcRuntime.SkeletonSessionLoginTime
            ?? throw SessionVirtualShapeGap(
                "no login instant was recorded when the skeleton session was built, so "
                + "\"Login Date\" / \"Login Time\" have no source — see "
                + "BcRuntime.SkeletonSessionLoginTime");

        return new SessionRow(
            connectionId, userName!, loginAt, ReadLoginTypeOrdinal(session), ReadHostName());
    }

    /// <summary>
    /// BC's own expression for field 18, reproduced: the direct <c>(int)</c> cast of
    /// <c>session.Authenticator.AuthenticationMethod</c>.
    /// </summary>
    /// <remarks>
    /// The cast really is direct in BC — <c>GetOptionValue(18, (int)AuthenticationMethod)</c> —
    /// even though the two do not line up semantically: NavClientCredentialType runs
    /// None = -1, Windows = 0, UserName = 1, NavUserPassword = 2, AccessControlService = 3, …
    /// while field 18 declares only <c>None,Windows,"Username and Password","Access Control
    /// Service"</c>. Being faithful to BC's CODE is the rule here, as it is for the Time Zone
    /// provider, so the cast is copied rather than "corrected" into a mapping BC does not do.
    ///
    /// Returns -1 for "cannot answer", which the caller turns into BC's own field default. A
    /// negative or out-of-range ordinal is the one case where copying the cast would write a
    /// value the option cannot render — and an option ordinal is a stored column value, so a
    /// wrong one mis-keys the row silently rather than failing loudly.
    /// </remarks>
    private static int ReadLoginTypeOrdinal(object session)
    {
        const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;
        var auth = session.GetType().GetProperty("Authenticator", F)?.GetValue(session);
        var method = auth?.GetType().GetProperty("AuthenticationMethod", F)?.GetValue(auth);
        if (method == null) return -1;
        try { return Convert.ToInt32(method); }
        catch (Exception) { return -1; }
    }

    /// <summary>
    /// The machine hosting this session — the runner's counterpart to BC's
    /// <c>DnsHelper.HostName</c>. Host-derived on purpose and by the same argument the Time
    /// Zone provider makes: when BC's own answer is a property of the machine, reading the
    /// machine is the faithful option and a fabricated constant is not.
    /// </summary>
    private static string ReadHostName()
    {
        // Environment.MachineName, not Dns.GetHostName(): the latter can do a name-service
        // lookup, and this runs inside a bundle load where a DNS stall would be paid by every
        // test in it. Both report the same name on a normal host.
        try
        {
            var name = Environment.MachineName;
            return string.IsNullOrEmpty(name) ? "localhost" : name;
        }
        catch (Exception)
        {
            // A host with no readable machine name still HAS a host name as far as BC's schema
            // is concerned; "localhost" is the one value that is true of every machine rather
            // than a guess about this one.
            return "localhost";
        }
    }

    /// <summary>
    /// One column of the Session row, matched by the metatable's own FIELD NAME so the mapping
    /// tracks whatever the System package in the resolved artifact declares rather than a
    /// hardcoded field-number table. "Database Name" and "Application Name" deliberately fall
    /// through to BC's own default — see this file's header and #3230.
    /// </summary>
    private static object? BuildSessionValue(NCLMetaField field, SessionRow row)
    {
        object? Default() => _aovGetDefaultNavValue!.Invoke(null, new object?[] { field, false });

        switch (NormalizeObjectTypeName(field.FieldName ?? string.Empty))
        {
            case "connectionid":
                return _aovNavIntegerCreate!.Invoke(null, new object?[] { row.ConnectionId });
            case "userid":
                return _aovNavTextCreateTruncated!.Invoke(
                    null, new object?[] { field.FieldDefinedLength, row.UserId });
            case "mysession":
                // BC's own constant, verbatim from SessionDataProvider: the single row this
                // table produces IS the reading session, so this is never conditional.
                // NavBoolean is not a compile-time reference here (it moved between
                // Runtime and Types across BC versions, which is why the shared binder
                // resolves it by name), so it goes through NavBoolean(bool) like every
                // other Boolean column in this directory.
                return NavBoolean(value: true);
            case "logindate":
                return NavDate.Create(row.LoginAt.Date);
            case "logintime":
                return NavTime.Create(
                    row.LoginAt.Hour, row.LoginAt.Minute, row.LoginAt.Second, row.LoginAt.Millisecond);
            case "logintype":
            {
                var ordinal = row.LoginTypeOrdinal;
                var members = field.FieldOptionMetadata?.OptionString?.Split(',').Length ?? 0;
                if (ordinal < 0 || members == 0 || ordinal >= members) return Default();
                return _aovNavOptionCreate!.Invoke(null, new object?[] { field.FieldOptionMetadata, ordinal });
            }
            case "hostname":
                return _aovNavTextCreateTruncated!.Invoke(
                    null, new object?[] { field.FieldDefinedLength, row.HostName });
            default:
                return Default();
        }
    }
}
