// RecordPatches.DateVirtualTable — the Date system virtual table (2000000007) is served by
// BC's OWN DateDataProvider, the way a service tier serves it.
//
// WHAT A SERVICE TIER DOES
//   DataAccessSource.GetVirtualDataAccess(table) builds
//
//       new DataAccess(session, GetVirtualDataProvider(table), globalFilters,
//                      tableVersionTokens, VirtualAndTempTransactionalDataCache.Instance)
//
//   and GetVirtualDataProvider answers `new DateDataProvider(session)` for 2000000007.
//   DateDataProvider is a RangeBasedComputedDataProvider — the same base IntegerDataProvider
//   has — so it computes one row per period on demand and stores none. Decompiled from
//   Ncl.dll (28.4.53241.54318): GetValuesWithinRangeForKeyField walks period starts from
//   DateTimeHelper.DatePeriodStartMinimumDate to DatePeriodStartMaximumDate for the period
//   type the preceding key field names, and CountValuesWithinRange counts the same span; the
//   union over a multi-range filter and the descending walk come from the base.
//
// WHAT THIS FILE DOES
//   Hands that same DataAccess out. GetDataAccessForTableCore calls GetDateVirtualDataAccess,
//   which goes through the shared GetBcVirtualDataAccess in
//   RecordPatches.IntegerVirtualTable.cs — so Count(), IsEmpty(), FindSet/Next/FindLast, a
//   keyed Get and every period column are answered by BC's own code, lazily, across years
//   1..9999.
//
// WHAT IT REPLACED: a materialised store behind a window of whole years (1900-01-01 ..
//   2099-12-31 by default), a per-request span ledger, a row cap, four request-carrying
//   guards and a FlowField-side net. That store could not follow a range open at one end,
//   because BC runs the open end out to its own first or last period start for the period
//   type, so such a range was either answered from the window — a row-count divergence — or
//   refused outright. docs/limitations.md#date-virtual-table has what that cost and #3506 has
//   why it stayed as long as it did.
//
// PRECOMPILED-DLL RESPECT
//   No BC body is rewritten or replaced. This calls BC's own factory method and hands back
//   the object it built; the provider that answers every read is Microsoft's.
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int DateVirtualTableId = 2000000007;

    /// <summary>True if <paramref name="table"/> is the Date system virtual table (2000000007).</summary>
    private static bool IsDateVirtualTable(NCLMetaTable? table)
        => table != null && table.TableId == DateVirtualTableId;

    /// <summary>The DataAccess BC's own factory builds for 2000000007 — one over DateDataProvider.</summary>
    /// <remarks>
    /// No skeleton-session check here any more. The materialising populator needed one, because
    /// it called BC's DateDataProvider.GetPeriodName itself; BC's own provider takes the session
    /// from the DataAccessSource that built it, which is the same object either way.
    /// </remarks>
    internal static object GetDateVirtualDataAccess(object dataAccessSource, NCLMetaTable table)
        => GetBcVirtualDataAccess(dataAccessSource, table,
            "every Record Date read would answer 'There is no Date within the filter'");
}
