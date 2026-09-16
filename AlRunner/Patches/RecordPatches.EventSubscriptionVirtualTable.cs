// RecordPatches.EventSubscriptionVirtualTable — the Event Subscription system virtual table
// (2000000140) is served by BC's OWN EventSubscriptionDataProvider, the way a service tier
// serves it (#4198).
//
// WHAT A SERVICE TIER DOES
//   DataAccessSource.GetVirtualDataAccess(table) builds a DataAccess over
//   GetVirtualDataProvider(table), which answers `new EventSubscriptionDataProvider(session)`
//   for 2000000140. That provider's GetAllItemsInternal reads
//
//       NavGlobal.EventSubscriptionMetadata.GetDistinctEventSubscriptions(
//           navAppGroup?.GroupId, publisherObjectId, subscriberObjectId)
//
//   and projects each NavEventSubscription into the table's 13 columns. Decompiled from Ncl.dll
//   28.4.53241.54346 (sha256 6f2cf682...) and 28.1.49838.53910.
//
// WHY IT IS NOT THE #4147 MECHANISM
//   #4147 grouped four tables under "no provider, falls through to an empty temp store". Three
//   of them read NCLMetadata.GetSnapshotOfAllObjects, which a Cecil rewrite empties. This one
//   does not read the snapshot at all — it reads a registry keyed by subscription — so neither
//   that diagnosis nor the three-piece template those siblings use applies here. #4198 is the
//   separate issue; its fix is the registry, not a row projection.
//
// WHAT MAKES THE ROWS APPEAR
//   EventSubscriberPatches.SeedSubscriptionMetadata constructs BC's own
//   NavEventSubscriptionMetadata, writes it to NavSystemTenant.eventSubscriptionMetadata (null
//   on the skeleton tenant, which is made with GetUninitializedObject), and appends the very
//   NavEventSubscription objects the dispatch path already builds. One scanned
//   [NavEventSubscriber] inventory, two registries.
//
// PRECOMPILED-DLL RESPECT
//   No BC body is rewritten or replaced. This calls BC's own factory method and hands back the
//   object it built; the provider that answers every read is Microsoft's.
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int EventSubscriptionVirtualTableId = 2000000140;

    /// <summary>True if <paramref name="table"/> is the Event Subscription system virtual
    /// table (2000000140).</summary>
    private static bool IsEventSubscriptionVirtualTable(NCLMetaTable? table)
        => table != null && table.TableId == EventSubscriptionVirtualTableId;

    /// <summary>
    /// The DataAccess BC's own factory builds for 2000000140 — one over its
    /// <c>EventSubscriptionDataProvider</c>.
    /// </summary>
    /// <remarks>
    /// Routed through the shared <see cref="GetBcVirtualDataAccess"/>, which refuses rather than
    /// falling back to the temp store: a fallback would restore exactly the empty-store answer
    /// this table is being taken off, and an empty Event Subscription table is
    /// indistinguishable from a correct one for any AL that merely counts rows.
    /// </remarks>
    internal static object GetEventSubscriptionVirtualDataAccess(
        object dataAccessSource, NCLMetaTable table)
        => GetBcVirtualDataAccess(dataAccessSource, table,
            "every Record \"Event Subscription\" read would answer from an empty store");
}
