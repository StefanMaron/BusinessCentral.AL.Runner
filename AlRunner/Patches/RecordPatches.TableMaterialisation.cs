// RecordPatches.TableMaterialisation — the create → hydrate → hand-out step for one table's
// in-memory storage, and the gate that stops a second thread being handed it half-built.
//
// THE DEFECT THIS CLOSES (#2788)
//   GetDataAccessForTableCore materialises a table's storage in three steps: create it, run the
//   --test-data on-demand load into it (#2262), hand it back. Only the GetOrAdd winner ran the
//   load — correctly, since a loser would be hydrating storage it is about to throw away — but
//   the loser did not WAIT for it. It was handed the winner's instance the moment the winner
//   published it, which is before the winner's rows exist.
//
//   Harmless on the generic path only by luck; on the Object Metadata (2000000071) branch it
//   inverted the rule that branch is built on. That table is a real SQL table, so a backup can
//   genuinely carry rows for it, and PopulateObjectMetadataSystemTable's contract is "synthesise
//   43 rows only if nobody else put any there". A loser handed the store mid-hydration ran that
//   check against an empty store, claimed the once-per-provider populate flag and synthesised —
//   and the winner's real rows then landed on top of a store that was no longer empty. Wrong
//   rows, no error, unchanged exit code, and the synthesised ids are a subset of what a real
//   backup carries for the same table, so the result looks entirely plausible.
//
// THE INVARIANT
//   Nobody leaves this method for a given (DataAccessSource, table id) while a --test-data load
//   for it is still running. Every caller therefore sees storage that is either fully hydrated
//   or will never be hydrated — never a snapshot taken mid-flight.
//
// WHY PUBLICATION STILL HAPPENS BEFORE HYDRATION
//   The obvious alternative — hold the instance back until it is hydrated — is wrong here.
//   HydrateTestDataTable inserts through perTable's own entry (it does its own GetOrAdd on it),
//   so an unpublished instance would be hydrated into a *different* store and handed back empty.
//   What the gate protects is not the publication, it is the HAND-OUT.
//
// WHY THE WAIT GRAPH CANNOT CYCLE
//   Hydrating table X runs BC's own metadata and NavValue construction, and that code can reach
//   a Record of another table Y and land straight back here. If such a nested call could block
//   on Y's gate while the thread holding Y reached back for X, the two would deadlock (ABBA) —
//   a hang traded for a wrong row, which is no trade at all. So a thread that already holds a
//   gate never takes another: it uses the same lock-free create-and-publish the pre-gate code
//   used, which is exactly what a nested call did before, since InvokeTestDataOnDemandLoader
//   refuses a nested load anyway (_testDataLoadDepth). Waits only ever originate from threads
//   holding no gate, so the graph is acyclic by construction.
//
// COST WHEN --test-data IS NOT PASSED (every corpus and runner-extras leg)
//   Zero. With no loader installed nothing can hydrate, there is no window to protect, and the
//   code below is the original lock-free TryGetValue / create / GetOrAdd, with no gate object
//   allocated and no lock taken.
//
// KNOWN, TRACKED, AND DELIBERATELY NOT CHANGED HERE
//   A table whose storage was first created by a NESTED call is published without ever being
//   hydrated, and "storage presence IS the have-we-loaded-this answer" then means no later
//   touch loads it either — so that table silently keeps none of its backup rows for the whole
//   run. That is a second defect of the same family, with a different fix and a different risk
//   (hydrating into a store AL may already have written to, and, for 2000000071, into one the
//   populator may already have synthesised into). It is filed separately rather than folded in;
//   the behaviour below is byte-for-byte what it was.
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// One gate per (DataAccessSource, table id). Its monitor is what a second thread waits on;
    /// <see cref="Materialised"/> is what lets every later touch skip the monitor entirely.
    /// </summary>
    private sealed class TableMaterialisationGate
    {
        private volatile bool _materialised;

        /// <summary>Set only inside the gate, after the loader has returned. Reading it true
        /// therefore means no hydration for this (source, table) is in flight.</summary>
        internal bool Materialised => _materialised;

        internal void MarkMaterialised() => _materialised = true;
    }

    private static readonly ConditionalWeakTable<object, ConcurrentDictionary<int, TableMaterialisationGate>>
        _materialisationGates = new();

    /// <summary>How many gates this thread holds. Non-zero means "already inside a
    /// materialisation", which is the one state in which this method must not block — see the
    /// wait-graph note in this file's header.</summary>
    [ThreadStatic] private static int _materialisationDepth;

    /// <summary>
    /// The storage for <paramref name="tableId"/> on <paramref name="self"/>, created and
    /// --test-data-hydrated if it does not exist yet. Never returns storage whose hydration is
    /// still running.
    /// </summary>
    private static object GetOrCreateHydratedDataAccess(
        object self, ConcurrentDictionary<int, object> perTable, NCLMetaTable table, int tableId)
        => GetOrCreateHydratedDataAccessCore(self, perTable, tableId,
            () => _mCreateTempDataAccess!.Invoke(self, new object[] { table })!);

    /// <summary>
    /// The ordering itself, with the one BC-specific step (constructing the temp DataAccess)
    /// behind <paramref name="createStorage"/> so the ordering can be driven — and raced —
    /// without a booted engine. See AlRunner.Tests/TableMaterialisationOrderingTests.cs.
    /// </summary>
    internal static object GetOrCreateHydratedDataAccessCore(
        object self, ConcurrentDictionary<int, object> perTable, int tableId, Func<object> createStorage)
    {
        // No --test-data loader installed: nothing can hydrate, so there is no create → hydrate
        // window for anyone to observe. Original shape, original cost.
        if (TestDataOnDemandLoader == null)
        {
            if (perTable.TryGetValue(tableId, out var plain)) return plain;
            return perTable.GetOrAdd(tableId, createStorage());
        }

        var gates = _materialisationGates.GetValue(self,
            static _ => new ConcurrentDictionary<int, TableMaterialisationGate>());
        var gate = gates.GetOrAdd(tableId, static _ => new TableMaterialisationGate());

        // Fast path for every touch after the first. The perTable probe stays part of the
        // condition: a store reset (ResetPerTestState / RestoreInstallBaselineSnapshot) drops
        // the entry without touching the gate, and a dropped entry has to be built again.
        if (gate.Materialised && perTable.TryGetValue(tableId, out var ready)) return ready;

        // Already inside a materialisation on this thread — must not wait on anyone. This is
        // the pre-gate code path verbatim, and it is what a nested call did before.
        if (_materialisationDepth > 0)
        {
            if (perTable.TryGetValue(tableId, out var nested)) return nested;
            return perTable.GetOrAdd(tableId, createStorage());
        }

        lock (gate)
        {
            if (gate.Materialised && perTable.TryGetValue(tableId, out var late)) return late;

            _materialisationDepth++;
            try
            {
                object instance;
                var thisCallCreatedIt = false;
                if (perTable.TryGetValue(tableId, out var existing))
                {
                    // Published by a nested call (this thread's or another's) or by a restore.
                    // Storage presence IS the "have we loaded this" answer — see the on-demand
                    // note in GetDataAccessForTableCore — so nothing is loaded for it here.
                    instance = existing;
                }
                else
                {
                    var created = createStorage();
                    instance = perTable.GetOrAdd(tableId, created);
                    thisCallCreatedIt = ReferenceEquals(instance, created);
                }

                // Only the creator loads: a loser would be hydrating storage it is about to
                // throw away, and the winner's rows are already in the returned instance.
                if (thisCallCreatedIt)
                    InvokeTestDataOnDemandLoader(self, tableId);

                gate.MarkMaterialised();
                return instance;
            }
            finally { _materialisationDepth--; }
        }
    }
}
