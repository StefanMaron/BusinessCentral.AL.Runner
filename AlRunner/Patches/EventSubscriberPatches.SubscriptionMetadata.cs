// EventSubscriberPatches.SubscriptionMetadata — seeds BC's OWN event-subscription registry,
// NavGlobal.EventSubscriptionMetadata, so the Event Subscription virtual table (2000000140)
// is answered by Microsoft's EventSubscriptionDataProvider rather than an empty store (#4198).
//
// THE TWO REGISTRIES, AND WHY ONE SOURCE FEEDS BOTH
//   A subscriber reaches BC through two independent pieces of state, and the runner used to
//   populate only the first:
//
//     dispatch   NavTriggerEventHandler -> NavEventScope.registeredSubscriptions
//                seeded by EventSubscriberPatches (this file's siblings) -- this is what makes
//                a subscriber actually FIRE, and what NCLMetaApplicationObject.IsEventSubscribed
//                consults now that #3576 stopped rewriting it (#4216).
//
//     inventory  NavGlobal.EventSubscriptionMetadata -> GetDistinctEventSubscriptions
//                read ONLY by EventSubscriptionDataProvider.GetAllItemsInternal, i.e. by AL
//                code reading `Record "Event Subscription"`. Never consulted for dispatch.
//
//   Both hold the same object type -- NavEventSubscription -- and the runner already builds
//   those, with BC's own 5-arg ctor, in BuildSubscription. So the inventory is fed from the
//   very handles the dispatch path uses: one scanned [NavEventSubscriber] inventory, two
//   registries. Measured: a probe bundle with one codeunit-event and one table-event
//   subscriber yields `total-count=2`, and a filter on a codeunit with no subscriber yields
//   `quiet-count=0`, so the rows discriminate rather than being a fixed answer.
//
// WHY THE REGISTRY IS NULL WITHOUT THIS
//   NavGlobal.EventSubscriptionMetadata is `SystemTenant.EventSubscriptionMetadata`, backed by
//   the instance field NavSystemTenant.eventSubscriptionMetadata. The runner manufactures its
//   skeleton tenant with GetUninitializedObject (BcRuntime.InjectSkeletonSystemTenant), so no
//   ctor runs and that field stays null -- measured, not inferred: a diagnostic print beside
//   the failing lookup read `NavGlobal.SystemTenant = NavSystemTenant` and
//   `NavGlobal.EventSubscriptionMetadata = NULL` on BC 28.1.49838.53910.
//
//   BC's own populate path cannot fill it here. InitializeBaseEvents/InitializeAppGroupEvents
//   both return early on `NavEnvironment.Instance.StandaloneInstance`, and they reach
//   NavEventSubscriptionFactory.FindAndCreateSubscriptionInstances, which walks published app
//   metadata the runner has no equivalent of.
//
// PRECOMPILED-DLL RESPECT
//   No BC body is rewritten. This constructs BC's own NavEventSubscriptionMetadata through its
//   own ctor, writes it to the field BC's own ctor would have written, and appends BC's own
//   NavEventSubscription objects to it. Every row the table returns is built by Microsoft's
//   EventSubscriptionDataProvider from that state. See .claude/rules/precompiled-dll-respect.md.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

public static partial class EventSubscriberPatches
{
    private const string SubscriptionMetadataSurface = "EventSubscriptionMetadata";

    private static object? _subscriptionMetadata;
    private static IList? _subscriptionMetadataList;
    private static readonly HashSet<MethodInfo> _subscriptionMetadataSeeded = new();

    /// <summary>
    /// How many subscriber methods this file has appended to BC's registry, and how many rows
    /// that registry now holds — <c>(-1, -1)</c> before the registry exists.
    ///
    /// <para>Exposed for <c>EventSubscriptionVirtualTableResetTests</c>, which drives
    /// <see cref="ResetForReload"/> in-process. The reload path is not reachable from a
    /// single-bundle runner invocation, so an AL fixture cannot see it: without this accessor a
    /// reset that silently stopped clearing would leave every AL-level test passing, which is
    /// exactly the hole a source-text assertion left open the first time.</para>
    ///
    /// <para>The two numbers are deliberately separate: they must move TOGETHER. A reset that
    /// cleared one and not the other is the drift this pair exists to make visible.</para>
    /// </summary>
    internal static (int SeededMethods, int RegistryRows) SubscriptionMetadataCountsForTests()
    {
        lock (_lock)
        {
            return _subscriptionMetadataList == null
                ? (-1, -1)
                : (_subscriptionMetadataSeeded.Count, _subscriptionMetadataList.Count);
        }
    }

    /// <summary>
    /// Ensure <c>NavGlobal.EventSubscriptionMetadata</c> is non-null and holds one
    /// <c>NavEventSubscription</c> per scanned <c>[NavEventSubscriber]</c> method.
    ///
    /// <para>Idempotent: each subscriber method is appended at most once, keyed on its
    /// <see cref="MethodInfo"/>, so the per-bundle re-entry
    /// <see cref="InjectAllUsingStoredLookup"/> performs tops the inventory up with newly
    /// loaded assemblies rather than duplicating it. <see cref="ResetForReload"/> clears that
    /// record and BC's registry together, which is what makes a --watch or --server reload of
    /// the same bundle identity re-seed from the fresh assembly instead of skipping every
    /// subscriber as "already seeded".</para>
    ///
    /// <para><b>A one-shot multi-bundle run accumulates across bundles</b>, because it reaches
    /// neither reset: Program.cs gates <c>BcRuntime.ResetForNewBundleReload</c> on watch mode.
    /// Each bundle still counts its OWN subscriptions exactly once — the MethodInfo key sees to
    /// that — but the inventory also lists the previous bundle's. Measured, with a control arm
    /// confirming AllObj does not behave this way; tracked as #4222 and recorded in
    /// docs/limitations.md#event-subscription-virtual-table. Single-bundle runs, which is every
    /// CI leg, are correct.</para>
    /// </summary>
    internal static void SeedSubscriptionMetadata()
    {
        if (_reflectionFailed || _navAppGroupBaseGroup == null) return;

        lock (_lock)
        {
            var list = EnsureSubscriptionMetadataRegistry();
            if (list == null) return;

            // NOT scoped to the current bundle, deliberately -- see the limitation recorded in
            // docs/limitations.md#event-subscription-virtual-table and issue #4222. The scan
            // registries this reads are process-wide and are cleared only by ResetForReload,
            // which Program.cs reaches in watch/server mode but NOT between the bundles of a
            // one-shot multi-bundle invocation. Three candidate scopes were measured and all
            // three still contained the previous bundle: RegisteredModules(),
            // GetModuleAppInfoFor(...).AppId, and CurrentBundleAssemblies(). Scoping this table
            // therefore needs a per-bundle marker the runner does not currently keep, which is
            // a process-model change rather than a change to this file.
            //
            // A single-bundle run -- every CI invocation and every ordinary local one -- is
            // correct: measured, both probe bundles pass every arm when run alone.
            foreach (var handle in EnumerateSubscriberHandles())
            {
                if (!_subscriptionMetadataSeeded.Add(handle.Method)) continue;
                object? subscription;
                try { subscription = BuildSubscription(handle); }
                catch (Exception ex)
                {
                    // A subscriber whose subscription cannot be built is already reported by the
                    // dispatch path, which runs first and is the one that matters for behaviour.
                    // Dropping it from the inventory silently would not be, so it is named here.
                    _subscriptionMetadataSeeded.Remove(handle.Method);
                    Console.Error.WriteLine(
                        $"[Subscribers] EventSubscriptionMetadata: could not build subscription for "
                        + $"{handle.DiagnosticName}: {ex.GetType().Name}: {ex.Message}");
                    continue;
                }
                if (subscription == null) { _subscriptionMetadataSeeded.Remove(handle.Method); continue; }
                list.Add(subscription);
            }

            if (list.Count > 0)
                Console.Error.WriteLine(
                    $"[Subscribers] EventSubscriptionMetadata seeded: rows={list.Count}");
        }
    }

    /// <summary>
    /// The live <c>eventSubscriptions</c> list inside <c>NavGlobal.EventSubscriptionMetadata</c>,
    /// constructing and installing the registry on first use.
    ///
    /// <para>Every BC member this needs is bound as REQUIRED. A null from any of them means the
    /// runner could not read BC's shape, which is unmeasurable rather than absent — carrying on
    /// would leave 2000000140 answering an empty store while every other signal said the seeding
    /// had run (.claude/rules/guards-need-a-third-state.md).</para>
    /// </summary>
    private static IList? EnsureSubscriptionMetadataRegistry()
    {
        if (_subscriptionMetadataList != null) return _subscriptionMetadataList;

        var navNcl = _tNavEventSubscription?.Assembly;
        if (navNcl == null) return null;

        var tMetadata = navNcl.GetType("Microsoft.Dynamics.Nav.EventSubscription.NavEventSubscriptionMetadata")
            ?? throw new BcShapeGapException(
                SubscriptionMetadataSurface, "NavEventSubscriptionMetadata",
                "type not found in Ncl — the Event Subscription virtual table (2000000140) is "
                + "served by BC's EventSubscriptionDataProvider, which reads this type via "
                + "NavGlobal.EventSubscriptionMetadata",
                "docs/limitations.md#event-subscription-virtual-table");

        var systemTenant = MetadataPatches_SkeletonSystemTenant();
        if (systemTenant == null) return null;

        var fTenantMetadata = BcShape.RequiredField(
            systemTenant.GetType(), "eventSubscriptionMetadata", SubscriptionMetadataSurface,
            "NavGlobal.EventSubscriptionMetadata reads SystemTenant.EventSubscriptionMetadata, "
            + "which exposes this field; without it the Event Subscription virtual table cannot "
            + "be populated");

        // Reuse whatever is already there — a future change that constructs the tenant properly
        // must not get a second, competing registry.
        var existing = fTenantMetadata.GetValue(systemTenant);
        if (existing == null)
        {
            // NavEventSubscriptionMetadata(exclusionFilters, settingsEntries, errorReporters) —
            // every argument is null-coalesced to an empty list inside the ctor, and the two
            // pieces of state this file needs (`eventSubscriptions`, `eventSubscriberSyncLock`)
            // are FIELD INITIALIZERS, so a null-argument construction yields a fully usable,
            // empty registry. Decompiled from Ncl 28.1/28.4; measured on 28.1.49838.53910,
            // where the constructed instance reported `eventSubscriptions = List count=0` and a
            // non-null sync lock.
            var ctor = FindThreeArgMetadataCtor(tMetadata);
            existing = ctor.Invoke(new object?[] { null, null, null });
            FieldPoke.SetInstance(fTenantMetadata, systemTenant, existing);
        }

        var fSubscriptions = BcShape.RequiredField(
            existing.GetType(), "eventSubscriptions", SubscriptionMetadataSurface,
            "GetDistinctEventSubscriptions walks this list; without it no row can be added");

        _subscriptionMetadata = existing;
        _subscriptionMetadataList = fSubscriptions.GetValue(existing) as IList
            ?? throw new BcShapeGapException(
                SubscriptionMetadataSurface, "NavEventSubscriptionMetadata.eventSubscriptions",
                "field does not hold an IList — BC's shape moved, and appending to it blindly "
                + "would leave the Event Subscription virtual table silently empty",
                "docs/limitations.md#event-subscription-virtual-table");
        return _subscriptionMetadataList;
    }

    private static ConstructorInfo FindThreeArgMetadataCtor(Type tMetadata)
    {
        ConstructorInfo? found = null;
        foreach (var c in tMetadata.GetConstructors(
                     BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
        {
            if (c.GetParameters().Length != 3) continue;
            if (found != null)
                throw new BcShapeGapException(
                    SubscriptionMetadataSurface, "NavEventSubscriptionMetadata..ctor",
                    "BC declares more than one 3-argument constructor, so the runner cannot tell "
                    + "which one builds an empty registry",
                    "docs/limitations.md#event-subscription-virtual-table");
            found = c;
        }
        return found ?? throw new BcShapeGapException(
            SubscriptionMetadataSurface, "NavEventSubscriptionMetadata..ctor",
            "the 3-argument constructor (exclusionFilters, settingsEntries, errorReporters) was "
            + "not found; the runner builds the registry through it because the skeleton tenant "
            + "is made with GetUninitializedObject and so never ran one",
            "docs/limitations.md#event-subscription-virtual-table");
    }

    /// <summary>
    /// One <see cref="SubscriberHandle"/> per scanned <c>[NavEventSubscriber]</c> method, across
    /// every registry this class keeps.
    ///
    /// <para>Three of those registries hold bare <see cref="MethodInfo"/> lists rather than
    /// handles, because their dispatch path does not need a publisher ordinal. A handle is
    /// synthesised for them with ordinal 0: <see cref="BuildSubscription"/> reads only
    /// <c>CodeunitType</c>, <c>CodeunitId</c> and <c>Method</c> — BC's
    /// <c>NavEventSubscription</c> ctor derives the publisher object, method name and event type
    /// from the <c>NavEventSubscriberAttribute</c> on the method itself, so the ordinal never
    /// reaches it. Measured: a codeunit-event subscriber seeded this way produces a row whose
    /// "Subscriber Function" is the real AL method name.</para>
    /// </summary>
    private static IEnumerable<SubscriberHandle> EnumerateSubscriberHandles()
    {
        foreach (var list in _byKey.Values)
            foreach (var handle in list) yield return handle;

        foreach (var vs in _validateSubs) yield return vs.Handle;

        foreach (var kv in _byCodeunitKey)
            foreach (var h in SynthesiseHandles(kv.Value, kv.Key.PublisherCodeunitId)) yield return h;

        foreach (var kv in _byTableEventKey)
            foreach (var h in SynthesiseHandles(kv.Value, kv.Key.PublisherTableId)) yield return h;

        foreach (var kv in _byObjectEventKey)
            foreach (var h in SynthesiseHandles(kv.Value, kv.Key.PublisherId)) yield return h;
    }

    private static IEnumerable<SubscriberHandle> SynthesiseHandles(
        List<MethodInfo> methods, int publisherId)
    {
        foreach (var m in methods)
        {
            var declaring = m.DeclaringType;
            if (declaring == null) continue;
            yield return new SubscriberHandle(
                declaring, TryReadCodeunitId(declaring), m, publisherId, 0,
                $"{declaring.Name}.{m.Name} -> publisher {publisherId}");
        }
    }

    /// <summary>The skeleton <c>NavSystemTenant</c>, or null when the tenant seam is unavailable
    /// (a runner started without one — every read of 2000000140 then keeps its current
    /// behaviour rather than failing here).</summary>
    private static object? MetadataPatches_SkeletonSystemTenant() => BcRuntime.SkeletonSystemTenant;
}
