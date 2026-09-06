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
//   A thread that holds no gate never leaves this method with storage whose --test-data load is
//   still running. What it is handed is either fully hydrated or will never be hydrated — never
//   a snapshot taken mid-flight. (A thread that already holds a gate is the one carve-out, and
//   it is deliberate: see the wait-graph note below. That is the pre-gate behaviour, unchanged,
//   and the defect it leaves open is #2877, tracked separately.)
//
// WHY THE LATCH NAMES A STORAGE INSTANCE AND NOT A (SOURCE, TABLE) PAIR
//   A table is materialised many times in a run, not once. ResetPerTestState() drains every
//   source's perTable at bundle start (TestExecutor) and at every install-baseline boundary
//   restore (RestoreInstallBaseline), and RestoreInstallBaselineSnapshot then REPLACES entries
//   with freshly built storage. A latch meaning "this (source, table) has been materialised at
//   some point" survives all of that and is stale from the second materialisation onward — and
//   a stale-true latch reopens the exact window this file exists to close, because the winner
//   publishes into perTable BEFORE entering the loader (see below), so the fast path's perTable
//   probe succeeds against a store that is present-and-EMPTY. Pairing a stale latch with a
//   present entry is therefore not a safe combination, and an earlier version of this fix
//   assumed it was.
//
//   So the latch records WHICH storage instance it was set for. Reset paths do not have to know
//   the gate exists: dropping or replacing perTable's entry produces a different instance, the
//   instance check fails, and the next toucher goes through the lock. There is one mechanism,
//   not a mechanism plus a list of resets that must remember to call it.
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
// THE NESTED PUBLICATION, AND THE DEBT IT LEAVES (#2877)
//   A table whose storage is first created by a NESTED call is published without ever being
//   hydrated, and "storage presence IS the have-we-loaded-this answer" then meant no later touch
//   loaded it either — so that table silently kept none of its backup rows for the whole run,
//   and TestDataProvisioner recorded no outcome for it, so the run could not report it either.
//   The store afterwards looked exactly like a table nothing had ever touched.
//
//   The nested publication itself cannot be avoided: hydrating there would recurse. So it is
//   recorded instead. _awaitingTestDataHydration names the storage INSTANCE that was published
//   without a load, and the next touch that is NOT inside a materialisation pays the debt into
//   that same instance, inside the gate, before handing it out. Same instance-naming discipline
//   as the settled latch above, and for the same reason: a reset that drops or replaces the
//   entry produces a different instance, so a stale debt can never be paid into the wrong store.
//
//   TWO THINGS THE DEBT MUST NOT DO, AND HOW EACH IS STOPPED
//   * MIX ROWS. If anything wrote into the store between the nested publication and the payment
//     — the nested caller inserting through the handle it was given is the realistic route — a
//     load landing on top would produce a store holding the backup's rows AND somebody else's.
//     That is the wrong-rows outcome the #2788 hand-out ordering exists to prevent. So the debt
//     is only paid into a store that is provably EMPTY, and written off otherwise. "Cannot tell"
//     is written off as well: unknown is not empty, the same discipline
//     RecordPatches.StoredTableCensus keeps. A write-off is never silent — it is reported per
//     table through TestDataDeferredLoadWriteOffNotifier, which is what makes the outcome
//     readable rather than a store that looks untouched (.claude/rules/loud-failures.md).
//   * LET A POPULATE SYNTHESISE FIRST. Object Metadata (2000000071) is the one table with a
//     populate after its materialisation, and PopulateObjectMetadataSystemTable's contract is
//     "synthesise only if nobody else put rows here". A nested first touch used to run that
//     populate against the empty store it had just published, claim the once-per-provider flag
//     and synthesise 43 rows — which would then be exactly the mixing case above, so the debt
//     would be written off and the backup's real rows lost for the run. MaterialiseObjectMetadata-
//     StoreCore holds the populate off while a load is owed instead. The nested caller sees an
//     EMPTY Object Metadata store for that moment; that is the deliberate choice, because the
//     alternative is synthesising rows that would have to be withdrawn, and nothing can withdraw
//     rows a caller has already read. Nothing is lost when the backup does not offer the table:
//     the debt is paid with zero rows, the store is still empty at the next touch, and the
//     populate then synthesises exactly as it always did.
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// One gate per (DataAccessSource, table id), for the lifetime of that source. Its monitor
    /// is what a second thread waits on; <see cref="IsSettled"/> is what lets a later touch skip
    /// the monitor entirely.
    /// </summary>
    private sealed class TableMaterialisationGate
    {
        /// <summary>The storage instance that last left the materialisation below, held WEAKLY.
        /// A strong reference would keep one generation of every table's rows alive past each
        /// ResetPerTestState(), which exists to drop exactly those rows. Weak costs nothing in
        /// correctness: a true answer is only ever returned to a caller that is holding the same
        /// instance (it just read it out of perTable), so the target cannot have been collected
        /// out from under a true answer, and a collected target answers false, which is the
        /// conservative direction — take the lock.</summary>
        private WeakReference<object>? _settled;

        /// <summary>True only for the exact storage instance this gate last saw out of the
        /// materialisation. It says "no --test-data load is running against THIS instance, and
        /// none ever will be" — deliberately not "this instance has rows", because the
        /// already-published branch settles an instance nothing loads (see there). Storage that
        /// a reset dropped and a later touch rebuilt is a different instance and fails here, so
        /// the rebuild is ordered by the lock like the first materialisation was.</summary>
        internal bool IsSettled(object store) =>
            Volatile.Read(ref _settled) is { } settled
            && settled.TryGetTarget(out var target)
            && ReferenceEquals(target, store);

        /// <summary>Called only inside the gate, after any load for <paramref name="store"/> has
        /// returned. A fresh WeakReference each time rather than SetTarget on a shared one:
        /// WeakReference&lt;T&gt; is not documented as safe for concurrent SetTarget/TryGetTarget,
        /// and publishing an immutable one through a volatile write is.</summary>
        internal void MarkSettled(object store) =>
            Volatile.Write(ref _settled, new WeakReference<object>(store));
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
            () => _mCreateTempDataAccess!.Invoke(self, new object[] { table })!,
            // The production emptiness probe. Every real caller passes it: a deferred load
            // settled without one would be paid BLIND, which is the row-mixing the debt exists
            // to avoid (#2877).
            StoredHasAnyRow);

    /// <summary>
    /// The storage for Object Metadata (2000000071): materialised like any other table, then
    /// populated with its synthesised fallback row set — except while a --test-data load is
    /// still owed for it, when the populate is held off so the backup's real rows still win.
    /// See the #2877 note in this file's header for why "hold off" and not "synthesise now".
    /// </summary>
    private static object MaterialiseObjectMetadataStore(
        object self, ConcurrentDictionary<int, object> perTable, NCLMetaTable table, int tableId)
        => MaterialiseObjectMetadataStoreCore(self, perTable, tableId,
            () => _mCreateTempDataAccess!.Invoke(self, new object[] { table })!,
            StoredHasAnyRow,
            store => PopulateObjectMetadataSystemTable(store, table));

    /// <summary>
    /// The ordering itself, with the two BC-specific steps behind delegates so it can be driven
    /// without a booted engine. See AlRunner.Tests/NestedTableMaterialisationHydrationTests.cs.
    /// </summary>
    internal static object MaterialiseObjectMetadataStoreCore(
        object self, ConcurrentDictionary<int, object> perTable, int tableId,
        Func<object> createStorage, Func<object, bool?>? storeHasAnyRow, Action<object> populate)
    {
        var store = GetOrCreateHydratedDataAccessCore(self, perTable, tableId, createStorage, storeHasAnyRow);

        // A store that still owes a --test-data load is left ALONE, empty, until the touch that
        // pays it. Synthesising into it now would make the payment a mix of real and synthesised
        // rows — and the payment refuses to mix, so it would be written off and the backup's real
        // rows lost for the run instead. Only reachable under --test-data: with no loader
        // installed nothing is ever owed and this is the pre-#2877 shape exactly.
        if (IsAwaitingTestDataHydration(store)) return store;

        populate(store);
        return store;
    }

    /// <summary>Storage instances published by a nested materialisation and therefore never
    /// --test-data-loaded. Keyed on the INSTANCE, so it dies with the store and no reset path
    /// has to know it exists — the same reason the settled latch names an instance.</summary>
    private static readonly ConditionalWeakTable<object, object> _awaitingTestDataHydration = new();

    private static readonly object _awaitingTestDataHydrationSentinel = new();

    /// <summary>
    /// True while <paramref name="store"/> owes a --test-data load it could not run when it was
    /// created, because it was created from inside another table's hydration (#2877). False for
    /// every store on a run without --test-data, and false again the moment the debt is settled
    /// — either paid or written off.
    /// </summary>
    internal static bool IsAwaitingTestDataHydration(object store)
        => _awaitingTestDataHydration.TryGetValue(store, out _);

    /// <summary>
    /// Does <paramref name="dataAccess"/>'s in-memory store hold a row right now?
    /// <c>true</c> / <c>false</c> / <c>null</c> = cannot tell, and cannot-tell is NOT empty:
    /// every caller here treats it as "do not load", because loading blind is what would mix
    /// rows. Same read, and the same three-way discipline, as
    /// RecordPatches.StoredTableCensus.CollectCensus.
    ///
    /// <para>Resolution goes through <see cref="PrivateMemberLookup"/> rather than
    /// <c>GetField</c>: <c>primaryTree</c> is private on <c>TempTableDataProvider</c> and BC's
    /// own <c>CrmTableConnection.CrmTestDataProvider</c> derives from it, where
    /// <c>GetField(NonPublic)</c> does not return a base class's private field (#2725).</para>
    /// </summary>
    private static bool? StoredHasAnyRow(object dataAccess)
    {
        object? primaryTree;
        try
        {
            var provider = GetDataProvider(dataAccess);
            if (provider == null) return null;
            var field = PrivateMemberLookup.Field(provider.GetType(), "primaryTree");
            if (field == null) return null;                  // BC's private layout moved — unknown
            primaryTree = field.GetValue(provider);
        }
        catch (TargetInvocationException) { return null; }
        catch (InvalidOperationException) { return null; }

        // A null tree is BC's own "no row was ever inserted".
        if (primaryTree == null) return false;
        if (primaryTree is not IEnumerable rows) return null;
        foreach (var _ in rows) return true;                 // one row is the whole answer
        return false;
    }

    /// <summary>
    /// Settle the debt a nested publication left against <paramref name="store"/>, from inside
    /// the gate and before the store is handed out. Pays it — runs the load that was refused —
    /// only when the store is provably empty; otherwise writes it off and reports why. Either
    /// way the debt is gone afterwards, so no later touch re-tries and no table is reported
    /// twice.
    /// </summary>
    private static void SettleDeferredTestDataLoad(
        object self, int tableId, object store, Func<object, bool?>? storeHasAnyRow)
    {
        // No probe at all means the caller staged this storage itself and knows nothing else
        // wrote to it — the ordering tests. Production always passes StoredHasAnyRow.
        var occupied = storeHasAnyRow == null ? false : storeHasAnyRow(store);

        if (occupied == false)
        {
            InvokeTestDataOnDemandLoader(self, tableId);
            return;
        }

        TestDataDeferredLoadWriteOffNotifier?.Invoke(tableId, occupied == true
            ? "its storage already held rows by the time a load could run, and loading on top "
              + "of them would mix the backup's rows with those"
            : "the runner could not read whether its storage already held rows, and loading "
              + "blind could mix the backup's rows with rows that are already there");
    }

    /// <summary>
    /// The ordering itself, with the one BC-specific step (constructing the temp DataAccess)
    /// behind <paramref name="createStorage"/> so the ordering can be driven — and raced —
    /// without a booted engine. See AlRunner.Tests/TableMaterialisationOrderingTests.cs.
    /// </summary>
    /// <summary>
    /// Test-only overload: no emptiness probe, because the caller stages the storage itself and
    /// nothing else writes to it. Production never takes this route — see
    /// <see cref="GetOrCreateHydratedDataAccess"/>, which always passes
    /// <see cref="StoredHasAnyRow"/>.
    /// </summary>
    internal static object GetOrCreateHydratedDataAccessCore(
        object self, ConcurrentDictionary<int, object> perTable, int tableId, Func<object> createStorage)
        => GetOrCreateHydratedDataAccessCore(self, perTable, tableId, createStorage, storeHasAnyRow: null);

    /// <param name="storeHasAnyRow">Answers "does this storage already hold a row" — true /
    /// false / null for cannot-tell — and is consulted only when a deferred load (#2877) is
    /// owed, to decide whether paying it would mix rows.</param>
    internal static object GetOrCreateHydratedDataAccessCore(
        object self, ConcurrentDictionary<int, object> perTable, int tableId, Func<object> createStorage,
        Func<object, bool?>? storeHasAnyRow)
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

        // Fast path for every touch after the first. Read the entry, then ask the gate about
        // THAT INSTANCE — never about the (source, table) pair. A store reset (ResetPerTestState
        // / RestoreInstallBaselineSnapshot) drops or replaces the entry without touching the
        // gate, so "an entry exists" and "the gate has settled" can both be true of two
        // different stores, one of them the empty one a winner has just published on its way
        // into the loader. That combination is the #2788 hand-out itself.
        if (perTable.TryGetValue(tableId, out var ready) && gate.IsSettled(ready)) return ready;

        // Already inside a materialisation on this thread — must not wait on anyone (see the
        // wait-graph note in this file's header), and must not load either, because a nested
        // load would recurse. So it creates and publishes lock-free, exactly as the pre-gate
        // code did, and RECORDS that the instance it published owes a load (#2877). Without
        // that record, "storage presence IS the have-we-loaded-this answer" made the omission
        // permanent for the rest of the run.
        if (_materialisationDepth > 0)
        {
            if (perTable.TryGetValue(tableId, out var nested)) return nested;
            var createdNested = createStorage();
            var publishedNested = perTable.GetOrAdd(tableId, createdNested);
            if (ReferenceEquals(publishedNested, createdNested))
            {
                _awaitingTestDataHydration.AddOrUpdate(publishedNested, _awaitingTestDataHydrationSentinel);
                // Recorded for the run's own reporting too, so TableOutcome can say "created
                // during another table's hydration" instead of the null that means "nothing ever
                // touched it" (#2240's argument, applied to this case).
                TestDataDeferredLoadNotifier?.Invoke(tableId);
            }
            return publishedNested;
        }

        lock (gate)
        {
            if (perTable.TryGetValue(tableId, out var late) && gate.IsSettled(late)) return late;

            _materialisationDepth++;
            try
            {
                object instance;
                var thisCallCreatedIt = false;
                var owesADeferredLoad = false;
                if (perTable.TryGetValue(tableId, out var existing))
                {
                    // Published by a nested call (this thread's or another's) or by a restore.
                    // Storage presence IS the "have we loaded this" answer — see the on-demand
                    // note in GetDataAccessForTableCore — so a restore's instance is loaded by
                    // construction and nothing runs for it here. A NESTED publication is the one
                    // case where presence and loadedness came apart, and it says so: the debt
                    // below is recorded against that exact instance, and this is the first touch
                    // that can settle it (#2877).
                    instance = existing;
                    owesADeferredLoad = _awaitingTestDataHydration.TryGetValue(existing, out _);
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
                else if (owesADeferredLoad)
                    SettleDeferredTestDataLoad(self, tableId, instance, storeHasAnyRow);

                // Settled either way — paid, written off, or never owed — so no later touch
                // re-tries and no table is reported twice. Removing before MarkSettled keeps the
                // two facts in the right order for anything reading them from the fast path.
                _awaitingTestDataHydration.Remove(instance);
                gate.MarkSettled(instance);
                return instance;
            }
            finally { _materialisationDepth--; }
        }
    }
}
