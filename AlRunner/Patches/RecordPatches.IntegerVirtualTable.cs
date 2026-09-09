// RecordPatches.IntegerVirtualTable — the Integer system virtual table (2000000026) is
// served by BC's OWN IntegerDataProvider, the way a service tier serves it. This file also
// hosts GetBcVirtualDataAccess, the one reflection bind onto BC's virtual-table factory, which
// RecordPatches.DateVirtualTable.cs calls for 2000000007 (#3506).
//
// WHAT A SERVICE TIER DOES
//   DataAccessSource.GetVirtualDataAccess(table) builds
//
//       new DataAccess(session, GetVirtualDataProvider(table), globalFilters,
//                      tableVersionTokens, VirtualAndTempTransactionalDataCache.Instance)
//
//   and GetVirtualDataProvider answers `new IntegerDataProvider(session)` for 2000000026.
//   IntegerDataProvider is a RangeBasedComputedDataProvider: it computes rows on demand and
//   stores none. Decompiled from Ncl.dll (28.4.53241.54318), its whole body is
//
//       GetValuesWithinRangeForKeyField: if (range.GetInclusiveIntegerBounds(-1e9, 1e9, ...))
//                                            for (thisKey = asc ? low : high; ...) yield return ...
//       CountValuesWithinRange:          if (range.GetInclusiveIntegerBounds(-1e9, 1e9, ...))
//                                            return checked(high - low + 1);
//
//   with the union over a multi-range filter and the descending walk supplied by its base
//   (RangeBasedComputedDataProvider.GetValuesWithinFilterForKeyField /
//   CountValuesWithinFilter, which iterate FilterExpression.ToRangeList).
//
// WHAT THIS FILE DOES
//   Hands that same DataAccess out. GetDataAccessForTableCore calls
//   GetIntegerVirtualDataAccess, which reflection-invokes BC's own private
//   DataAccessSource.GetVirtualDataAccess — so Count(), IsEmpty(), FindSet/Next/FindLast and
//   Get() are answered by BC's code, on BC's own ±1,000,000,000 clamp, lazily.
//
// WHAT IT REPLACED: a materialised store behind a base window and a row cap, which could
//   not follow a range open at one end. docs/limitations.md#integer-virtual-table has what
//   that cost and #3485 has why it stayed as long as it did.
//
// PRECOMPILED-DLL RESPECT
//   No BC body is rewritten or replaced. This calls BC's own factory method and hands back
//   the object it built; the provider that answers every read is Microsoft's.
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int IntegerVirtualTableId = 2000000026;

    /// <summary>True if <paramref name="table"/> is the Integer system virtual table (2000000026).</summary>
    private static bool IsIntegerVirtualTable(NCLMetaTable? table)
        => table != null && table.TableId == IntegerVirtualTableId;

    private static MethodInfo? _bcGetVirtualDataAccess;

    /// <summary>The DataAccess BC's own factory builds for 2000000026 — one over IntegerDataProvider.</summary>
    internal static object GetIntegerVirtualDataAccess(object dataAccessSource, NCLMetaTable table)
        => GetBcVirtualDataAccess(dataAccessSource, table,
            "every Record Integer read would answer from an empty store");

    /// <summary>
    /// The DataAccess a service tier builds for a virtual table — for 2000000026 that is one
    /// over BC's own <c>IntegerDataProvider</c>, for 2000000007 one over its
    /// <c>DateDataProvider</c>. BC caches it per table id on the DataAccessSource instance
    /// itself (<c>virtualDataAccesses</c>), so repeat handouts share one provider exactly as
    /// they do on a tier, and a new source starts clean.
    /// </summary>
    /// <remarks>
    /// A bind failure throws rather than falling back to the temp store: falling back would
    /// silently restore the empty-store answer these tables spent four issues escaping
    /// (#2350, #3438, #3485, #3506), and an empty Integer table makes every
    /// `dataitem(N; Integer)` report body simply not run.
    /// </remarks>
    internal static object GetBcVirtualDataAccess(
        object dataAccessSource, NCLMetaTable table, string whatBreaksWithoutIt)
    {
        var factory = _bcGetVirtualDataAccess ??= dataAccessSource.GetType().GetMethod(
            "GetVirtualDataAccess",
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, types: new[] { typeof(NCLMetaTable) }, modifiers: null)
            ?? throw new InvalidOperationException(
                "DataAccessSource.GetVirtualDataAccess(NCLMetaTable) not found — BC shape changed. "
                + $"Table {table.TableId} is served by BC's own computed data provider through it; "
                + "without it " + whatBreaksWithoutIt + ".");

        try
        {
            return factory.Invoke(dataAccessSource, new object[] { table })!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            // Rethrow the inner exception with its ORIGINAL stack: a bare `throw tie.InnerException`
            // restarts the trace here, which hides where in BC's own factory the failure was.
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;   // unreachable; the compiler cannot see that Throw() does not return
        }
    }
}
