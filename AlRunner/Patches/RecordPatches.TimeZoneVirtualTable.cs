// RecordPatches.TimeZoneVirtualTable — managed provider for the "Time Zone" (2000000164)
// system virtual table.
//
// WHY THIS EXISTS (issue #2584)
//   Time Zone is virtual on the service tier: its rows are computed on demand from the host's
//   installed time zones. It routed to the same empty in-memory store as every other table
//   here, so every read answered zero rows — Get() silently returned false and FindSet()
//   raised "There is no Time Zone within the filter."
//
// WHAT REAL BC ANSWERS
//   Microsoft.Dynamics.Nav.Runtime.TimeZoneDataProvider (Ncl.dll, BC 28.4.53241.53989)
//   declares TableId => 2000000164, and its whole body is:
//
//       int timeZoneNo = 1;
//       foreach (TimeZoneInfo systemTimeZone in TimeZoneInfo.GetSystemTimeZones())
//       {
//           buffer[0] = NavInteger.Create(timeZoneNo);
//           buffer[1] = NavText.CreateTruncated(maxIdLength, systemTimeZone.Id);
//           buffer[2] = NavText.CreateTruncated(maxDisplayNameLength, systemTimeZone.DisplayName);
//           timeZoneNo++;
//       }
//
//   Three columns, and the row set is whatever the HOST OPERATING SYSTEM reports, numbered
//   1..N in enumeration order. That is the entire specification.
//
// ── THE DIVERGENCE THIS CREATES, AND WHY IT IS THE RIGHT ANSWER ──────────────────────────
//   BC in the cloud runs on Windows, so GetSystemTimeZones() there returns Windows ids
//   ("W. Europe Standard Time"). The runner runs on Linux, where the same call returns IANA
//   ids ("Europe/Berlin"). "No." is a sequence number over that list, so the two hosts
//   disagree about the ids AND about the numbering.
//
//   This provider enumerates the host, exactly as BC's own code does. The alternative — a
//   hardcoded Windows id list, so the answers match a Windows-hosted tier — was considered
//   and rejected: fabricating Windows time zone ids on a Linux host is a silent fake of the
//   kind .claude/rules/loud-failures.md bars, it would be wrong in a way no test on this host
//   could catch, and the list goes stale every time Microsoft revises it. Being faithful to
//   BC's CODE is the honest option when BC's own answer is a property of the machine.
//
//   Consequence, recorded so a future reader hits a decision and not a mystery: on Linux the
//   runner reports IANA ids where a Windows-hosted SaaS tier reports Windows ids. This is a
//   deliberate, permanent divergence — see docs/limitations.md, "Time Zone ids follow the
//   host". Nothing reads this table today (measured: no corpus test, no runner-extras test,
//   and the two Microsoft bucket codeunits that grep-match are false positives —
//   ERMUserPersonalization reads UserPersonalization."Time Zone", a field on a different
//   table, and UTContactTable only carries [FEATURE] [Time Zone] tags), so nothing breaks
//   now; the point of writing it down is the test somebody writes later.
//
// FIELD NAMES ARE MEASURED, NOT ASSUMED
//   Resolved by compiling AL against real BC symbols and dumping the metatable through
//   RecordRef (BC 28.1): field 3 = "No." (Integer), field 1 = "ID" (Text), field 2 =
//   "Display Name" (Text). Matched here BY NAME off the live metatable, so a BC metadata
//   change says so instead of writing a value into the wrong column.
//
// PRECOMPILED-DLL RESPECT
//   Runtime-engine types only (NCLMetaTable, NCLMetaField, NavValue, ReadOnlyRecordBuffer,
//   TempTableDataProvider), reached through the same helpers the AllObj / Date / Page
//   Metadata providers resolve. No AL business-logic body is touched.

using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int TimeZoneVirtualTableId = 2000000164;

    // One-shot per provider, unlike AllObj's per-id memo: the host's time zone list cannot
    // change during a run, and "No." is a sequence over the whole list — topping up later
    // would renumber rows AL had already read.
    private static readonly ConditionalWeakTable<object, object> _tzPopulatedProviders = new();

    private static bool IsTimeZoneVirtualTable(NCLMetaTable? table)
        => table != null && table.TableId == TimeZoneVirtualTableId;

    /// <summary>
    /// Populate the in-memory store behind Time Zone (2000000164) with one row per time zone
    /// the HOST reports, numbered 1..N in enumeration order — the same order and numbering
    /// BC's own TimeZoneDataProvider produces from the same call.
    /// </summary>
    private static void PopulateTimeZoneVirtualTable(object dataAccess, NCLMetaTable metaTable)
    {
        EnsureAllObjReflection(metaTable);
        EnsureDataAccessProviderReflection(dataAccess);

        var provider = _pDataAccessDataProvider!.GetValue(dataAccess)
            ?? throw new RunnerOutOfScopeException(
                "Time Zone (virtual table 2000000164)",
                "time-zone-virtual-table — data access has no in-memory provider; see docs/scope.md");

        if (_tzPopulatedProviders.TryGetValue(provider, out _)) return;

        // Read once and materialise, so the numbering cannot shift if the host's list were
        // enumerated twice. Failure here is loud: an empty Time Zone table is the bug being
        // fixed, and answering with no rows would put it straight back.
        IReadOnlyList<TimeZoneInfo> zones;
        try
        {
            zones = TimeZoneInfo.GetSystemTimeZones().ToList();
        }
        catch (Exception ex)
        {
            throw new RunnerOutOfScopeException(
                "Time Zone (virtual table 2000000164)",
                "time-zone-virtual-table — the host reports no usable time zone database "
                + $"({ex.GetType().Name}: {ex.Message}). BC's own TimeZoneDataProvider reads the "
                + "same TimeZoneInfo.GetSystemTimeZones(), so there is no other source to answer "
                + "from, and an empty table would be a wrong answer rather than a missing one. "
                + "See docs/scope.md");
        }

        for (var i = 0; i < zones.Count; i++)
        {
            var number = i + 1;   // BC starts at 1, not 0
            var zone = zones[i];
            InsertVirtualRow(provider, metaTable,
                new object[] { TimeZoneVirtualTableId, number, 0, 0 },
                field => BuildTimeZoneValue(field, number, zone));
        }

        _tzPopulatedProviders.Add(provider, new object());
    }

    /// <summary>
    /// One column of a Time Zone row, matched by the metatable's own FIELD NAME. Text columns
    /// are truncated to the column's declared length through BC's own NavText.CreateTruncated,
    /// exactly as TimeZoneDataProvider does — an IANA id is shorter than the column, but a
    /// display name on some hosts is not.
    /// </summary>
    private static object? BuildTimeZoneValue(NCLMetaField field, int number, TimeZoneInfo zone)
    {
        object? Text(string s) => _aovNavTextCreateTruncated!.Invoke(
            null, new object?[] { field.FieldDefinedLength, s ?? string.Empty });

        return NormalizeObjectTypeName(field.FieldName ?? string.Empty) switch
        {
            // "No." normalizes with its trailing dot intact; "no" is accepted too so a BC
            // version that drops the dot still matches rather than silently defaulting.
            "no." or "no" => _aovNavIntegerCreate!.Invoke(null, new object?[] { number }),
            "id" => Text(zone.Id),
            "displayname" => Text(zone.DisplayName),
            _ => _aovGetDefaultNavValue!.Invoke(null, new object?[] { field, false }),
        };
    }
}
