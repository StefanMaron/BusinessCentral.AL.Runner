// EventSubscriberPatches.PageTriggerEvents — the PAGE half of BC's implicit trigger events
// (issue #3436). Table trigger events (OnBeforeInsertEvent … ordinals 1-10) are wired in
// EventSubscriberPatches.cs; a page publishes its own fixed set, ordinals 11-19, through
// NCLMetaForm.pageTriggerEventHandler, and nothing wired that.
//
// Mechanism, and why it is a registration rather than a dispatcher: NavForm's own
// RaiseOnModifyRecordAsync (and its eight siblings) already runs — RunnerPageInstance
// .InvokeRecordTrigger calls it — and ends with
//
//     if (EventReadyToFire(NavTriggerEventType.OnModifyRecordEvent))
//         await nclMetaForm.PageTriggerEventHandler.OnModifyRecordEventAsync(
//             this, sourceObject, SourceTable?.OldRecord, allowAction);
//
// so BC fires the event itself, from its own code, once the page's NavEventScope holds a
// subscription. Before this file the scope was always empty: a page-scoped subscriber was
// filed under _byObjectEventKey — the registry for a manually-declared [IntegrationEvent] a
// page's own AL raises (#1794) — which BC's implicit-event path never consults. Registered,
// never dispatched, and silent in both directions.
//
// See docs/page-trigger-events.md for the per-event argument table and the ordinals' source.

using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class EventSubscriberPatches
{
    /// <summary>
    /// Page-scoped subscribers to BC's implicit page trigger events, keyed by
    /// (page object id, <c>NavTriggerEventType</c> ordinal). The page twin of
    /// <c>_byKey</c>.
    /// </summary>
    private static readonly Dictionary<Key, List<SubscriberHandle>> _byPageKey = new();

    /// <summary>
    /// Which subscriber methods are already in which <c>NavEventScope</c>, by scope identity.
    ///
    /// Deliberately NOT the flat <c>_injectedSubscriberMethods</c> set the table path uses. A
    /// page's <c>NCLMetaForm</c> can be rebuilt (<c>CopyLifetimeData</c> carries the handler
    /// across when it is, but a fresh build does not), and a flat "already injected" set would
    /// then refuse to re-register into the new, empty scope — the subscriber would fire until
    /// the first rebuild and silently stop after it. Keying on the scope object means a new
    /// scope is simply a scope we have not populated yet.
    /// </summary>
    private static readonly Dictionary<object, HashSet<MethodInfo>> _pageScopeContents =
        new(ReferenceEqualityComparer.Instance);

    private static FieldInfo? _fPageTriggerEventHandler;   // NCLMetaForm.pageTriggerEventHandler
    private static ConstructorInfo? _ciNavPageTriggerEventHandler;
    private static bool _pageReflectionResolved;
    private static bool _pageReflectionFailed;

    /// <summary>
    /// BC's <c>NavTriggerEventType</c> ordinals for the nine events a page publishes.
    ///
    /// Read off <c>NavForm</c>'s own <c>EventReadyToFire((NavTriggerEventType)N)</c> call sites
    /// and cross-checked against <c>NavPageTriggerEventHandler.On…EventAsync</c>'s
    /// <c>FireEventAsync(page, (NavTriggerEventType)N, …)</c> — the two agree on every one.
    /// Ordinals rather than enum names because the enum's names are not in the decompile's
    /// reach on every BC build, and the numbers are what both call sites carry.
    ///
    /// Returns 0 for anything else, which is how a manually-declared page event keeps its
    /// existing <c>_byObjectEventKey</c> route.
    /// </summary>
    internal static int ResolvePageEventOrdinalFromName(string name) => name switch
    {
        "OnOpenPageEvent" => 11,
        "OnClosePageEvent" => 12,
        "OnAfterGetRecordEvent" => 13,
        "OnAfterGetCurrRecordEvent" => 14,
        "OnNewRecordEvent" => 15,
        "OnInsertRecordEvent" => 16,
        "OnModifyRecordEvent" => 17,
        "OnDeleteRecordEvent" => 18,
        "OnQueryClosePageEvent" => 19,
        _ => 0,
    };

    /// <summary>
    /// File a page-scoped subscriber under its trigger-event ordinal.
    /// </summary>
    /// <returns>
    /// false when the name is not one of BC's nine, leaving the caller its own route.
    /// <paramref name="addedNew"/> distinguishes a first filing from a re-scan of the same
    /// method, so the caller's diagnostic count stays a count of new registrations.
    /// </returns>
    private static bool TryAddPageTriggerSubscriber(
        Type declaringType, int codeunitId, MethodInfo method, int pageId, string methodName,
        out bool addedNew)
    {
        addedNew = false;
        int ord = ResolvePageEventOrdinalFromName(methodName);
        if (ord == 0) return false;
        var key = new Key(pageId, ord);
        if (!_byPageKey.TryGetValue(key, out var list))
            _byPageKey[key] = list = new List<SubscriberHandle>();
        if (list.Any(h => h.Method == method)) return true;
        list.Add(new SubscriberHandle(declaringType, codeunitId, method, pageId, ord,
            $"{declaringType.Name}.{method.Name} → Page {pageId}/{methodName}"));
        addedNew = true;
        return true;
    }

    private static void EnsurePageReflection()
    {
        if (_pageReflectionResolved) return;
        _pageReflectionResolved = true;
        try
        {
            var navNcl = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl")
                ?? throw new InvalidOperationException("Microsoft.Dynamics.Nav.Ncl not loaded");
            var tNclMetaForm = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaForm")
                ?? throw new InvalidOperationException("NCLMetaForm not found");
            _fPageTriggerEventHandler = tNclMetaForm.GetField("pageTriggerEventHandler",
                    BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("NCLMetaForm.pageTriggerEventHandler not found");
            var tHandler = navNcl.GetType("Microsoft.Dynamics.Nav.EventSubscription.NavPageTriggerEventHandler")
                ?? throw new InvalidOperationException("NavPageTriggerEventHandler not found");
            _ciNavPageTriggerEventHandler = tHandler.GetConstructor(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, Type.EmptyTypes, null)
                ?? throw new InvalidOperationException("NavPageTriggerEventHandler parameterless ctor not found");
        }
        catch (Exception ex)
        {
            _pageReflectionFailed = true;
            Console.Error.WriteLine("[Subscribers] page trigger-event wiring disabled: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Register every discovered page trigger-event subscriber into its page's own
    /// <c>NavEventScope</c>, so BC's <c>NavForm.RaiseOn…Async</c> finds it and fires.
    ///
    /// Runs on the same schedule as the table-side bulk pass, and is safe to run repeatedly: a
    /// page whose <c>NCLMetaForm</c> does not exist yet is skipped and picked up on the next
    /// pass, and a subscriber already present in a given scope is not added twice.
    /// </summary>
    internal static void InjectPageTriggerSubs()
    {
        if (_reflectionFailed || _navAppGroupBaseGroup == null) return;
        EnsurePageReflection();
        if (_pageReflectionFailed) return;
        try { EnsureRegistryFresh(); } catch { }
        if (_byPageKey.Count == 0) return;

        int injected = 0, failed = 0, skipped = 0;
        lock (_lock)
        {
            foreach (var kv in _byPageKey)
            {
                int pageId = kv.Key.PublisherId;
                int ord = kv.Key.EventTypeOrdinal;

                var handler = TryGetOrCreatePageTriggerEventHandler(pageId);
                if (handler == null) { skipped += kv.Value.Count; continue; }

                var ordEnum = Enum.ToObject(_tNavTriggerEventType!, ord);
                var createIfNotFound = Enum.ToObject(_tEventScopeGetOption!, 1); // CreateIfNotFound
                object? scope;
                try { scope = _miGetEventScope!.Invoke(handler, new object?[] { ordEnum, createIfNotFound }); }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
                    Console.Error.WriteLine($"[Subscribers] page GetEventScope({pageId},{ord}) failed: " +
                        $"{inner.GetType().Name}: {inner.Message}");
                    failed += kv.Value.Count;
                    continue;
                }
                if (scope == null) { failed += kv.Value.Count; continue; }

                if (!_pageScopeContents.TryGetValue(scope, out var present))
                    _pageScopeContents[scope] = present = new HashSet<MethodInfo>();

                var newOnes = new List<object>();
                foreach (var sub in kv.Value)
                {
                    if (!present.Add(sub.Method)) continue;
                    object? subscription;
                    try { subscription = BuildSubscription(sub); }
                    catch (Exception ex)
                    {
                        var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
                        Console.Error.WriteLine($"[Subscribers] page BuildSubscription failed for " +
                            $"{sub.DiagnosticName}: {inner.GetType().Name}: {inner.Message}");
                        failed++;
                        continue;
                    }
                    if (subscription == null) { failed++; continue; }
                    newOnes.Add(subscription);
                    injected++;
                }
                if (newOnes.Count == 0) continue;

                var existing = (Array?)_fRegisteredSubscriptions!.GetValue(scope);
                int oldLen = existing?.Length ?? 0;
                var merged = Array.CreateInstance(_tNavEventSubscription!, oldLen + newOnes.Count);
                if (existing != null && oldLen > 0) Array.Copy(existing, 0, merged, 0, oldLen);
                for (int i = 0; i < newOnes.Count; i++) merged.SetValue(newOnes[i], oldLen + i);
                Infrastructure.FieldPoke.SetInstance(_fRegisteredSubscriptions, scope, merged);
            }
        }

        if (injected > 0 || failed > 0)
            Console.Error.WriteLine($"[Subscribers] page trigger-inject: injected={injected} " +
                $"failed={failed} skipped-no-metaform={skipped} keys={_byPageKey.Count}");
    }

    /// <summary>
    /// The <c>NavPageTriggerEventHandler</c> for <paramref name="pageId"/>, creating one if the
    /// page's <c>NCLMetaForm</c> carries none.
    ///
    /// BC assigns it in <c>NCLMetaForm.TransferEventsAndTriggersFromPreviousInstance</c>, which
    /// runs on the metadata-load path; the create-if-null here is the same guard
    /// <see cref="CreateTableTriggerEventHandler"/> exists for on the table side, and for the
    /// same reason — <c>NavForm.RaiseOnModifyRecordAsync</c> dereferences
    /// <c>PageTriggerEventHandler</c> unconditionally once the event is subscribed, so a null
    /// there surfaces as a bare NRE inside BC rather than as a missing event.
    /// </summary>
    /// <returns>null when the page has no metaform yet — a skip, not a failure.</returns>
    private static object? TryGetOrCreatePageTriggerEventHandler(int pageId)
    {
        NCLMetaForm? metaForm;
        try { metaForm = NavGlobal.NCLMetadata.GetMetaFormById(pageId, requireCompiled: true); }
        catch (Exception) { return null; }   // NavMetadataNotFoundException and friends: not loaded yet
        if (metaForm == null) return null;

        object? handler;
        try { handler = _fPageTriggerEventHandler!.GetValue(metaForm); }
        catch { handler = null; }
        if (handler != null) return handler;

        try
        {
            handler = _ciNavPageTriggerEventHandler!.Invoke(null);
            Infrastructure.FieldPoke.SetInstance(_fPageTriggerEventHandler!, metaForm, handler);
            return handler;
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            Console.Error.WriteLine($"[Subscribers] NavPageTriggerEventHandler ctor failed for page " +
                $"{pageId}: {inner.GetType().Name}: {inner.Message}");
            return null;
        }
    }
}
