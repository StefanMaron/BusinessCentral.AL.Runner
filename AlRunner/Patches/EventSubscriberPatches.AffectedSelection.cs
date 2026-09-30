// EventSubscriberPatches.AffectedSelection — the subscriber side of affectedOnly's event keys (#4988).
// docs/server-mode.md#affectedonly-and-event-subscribers has the selection rules.
using System.Reflection;
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

/// <summary>One <c>[EventSubscriber]</c> method of a request module. <see cref="Identity"/> changes
/// whenever anything about the binding does (publisher, event, element, attribute arguments, the
/// codeunit's manual binding); <see cref="EventKey"/> is what it subscribes to, in
/// <see cref="AlEventRaiseTracker"/>'s key space.</summary>
internal readonly record struct SubscriberBinding(
    string SubscriberKind, int SubscriberId, string ProcedureName, string EventKey, string Identity);

/// <summary>The request modules as the recording seed pass found them.</summary>
/// <param name="ObservableEventKeys">Events whose raises reach the dispatcher, so a test that raised
/// one has it in its recorded set.</param>
/// <param name="PublisherObjects">Every AL object class of the request modules (<c>Kind|id</c>),
/// with whether all its event scopes were seeded.</param>
internal sealed record EventObservability(
    HashSet<string> ObservableEventKeys, Dictionary<string, bool> PublisherObjects);

public static partial class EventSubscriberPatches
{
    private static readonly Dictionary<Assembly, (HashSet<string> Objects, bool Seeded)> _recordingSeedByAssembly
        = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Seed <c>γeventScope</c> on every event scope of the request's modules, so each raise of
    /// one of their events reaches <c>DispatchCore</c> (and <see cref="AlEventRaiseTracker"/>)
    /// even with no subscriber. Observably equivalent: an unseeded publisher returns from
    /// <c>if (γeventScope == null &amp;&amp; !IsEventSessionRecorderEnabled) return</c> (every emitted
    /// publisher, <c>--dump-csharp</c>), and a seeded one reaches <c>DispatchCore</c>, which
    /// returns on an empty subscriber list — the path every subscribed event already takes (#4988).
    /// Idempotent per assembly; never overwrites a non-null field.
    /// </summary>
    internal static EventObservability SeedAllEventScopesForRecording()
    {
        var sentinel = EnsureSentinelNavEventScope();
        var publishers = new Dictionary<string, bool>(StringComparer.Ordinal);
        lock (_lock)
        {
            foreach (var asm in BcRuntime.StampedModuleAssemblies())
            {
                if (!_recordingSeedByAssembly.TryGetValue(asm, out var state)
                    || (!state.Seeded && sentinel != null))
                    _recordingSeedByAssembly[asm] = state = SeedAssembly(asm, sentinel);
                foreach (var o in state.Objects)
                    publishers[o] = (publishers.TryGetValue(o, out var s) ? s : true) && state.Seeded;
            }

            var observable = new HashSet<string>(StringComparer.Ordinal);
            foreach (var scope in _seededScopeTypes)
            {
                if (BcRuntime.IsStaleBundleAssembly(scope.Assembly)) continue;
                if (AlEventRaiseTracker.EventScopeKey(scope) is { } k) observable.Add(k);
            }
            return new EventObservability(observable, publishers);
        }
    }

    // Caller holds _lock.
    private static (HashSet<string> Objects, bool Seeded) SeedAssembly(Assembly asm, object? sentinel)
    {
        var objects = new HashSet<string>(StringComparer.Ordinal);
        Type?[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Subscribers] recording seed: cannot read types of {asm.GetName().Name}: {ex.GetType().Name}");
            return (objects, false);
        }

        bool complete = sentinel != null && types.All(t => t != null);
        foreach (var t in types)
        {
            if (t == null) continue;
            if (!t.IsNested)
            {
                if (BcRuntime.TryDecodeEventPublisherDeclType(t.Name, out var kind, out var id))
                    objects.Add($"{AlEventRaiseTracker.NormalizeDispatchKind(kind)}|{id}");
                continue;
            }
            if (sentinel == null || !t.Name.EndsWith("_Scope", StringComparison.Ordinal)) continue;
            var fld = EventScopeField(t);
            if (fld == null) continue; // an ordinary procedure scope
            // #5004: an extension's event is keyed by its base object; one whose base cannot be
            // named cannot be keyed, so no publisher of this module counts as fully recorded.
            if (t.DeclaringType is { } decl
                && BcRuntime.TryDecodeExtensionEventPublisherDeclType(decl.Name, out _, out _, out _)
                && AlEventRaiseTracker.EventScopeKey(t) == null)
                complete = false;
            if (_seededScopeTypes.Contains(t)) continue;
            try
            {
                if (fld.GetValue(null) == null) fld.SetValue(null, sentinel);
                _seededScopeTypes.Add(t);
            }
            catch (Exception ex)
            {
                complete = false;
                Console.Error.WriteLine($"[Subscribers] recording seed failed on {t.FullName}: {ex.Message}");
            }
        }
        return (objects, complete);
    }

    /// <summary>Every <c>[EventSubscriber]</c> of the request modules (see
    /// <see cref="BcRuntime.StampedModuleAssemblies"/>), read from the loaded code. Null when an
    /// attribute could not be read — the caller cannot then tell which bindings changed.</summary>
    internal static List<SubscriberBinding>? CurrentModuleSubscriberBindings()
    {
        var result = new List<SubscriberBinding>();
        foreach (var asm in BcRuntime.StampedModuleAssemblies())
        {
            List<MethodInfo> methods;
            try { methods = AssemblyTypeIndex.For(asm).FindAttributedMethods("Codeunit", "NavEventSubscriberAttribute"); }
            catch { return null; }
            foreach (var m in methods)
            {
                var t = m.DeclaringType;
                if (t == null || !BcRuntime.TryDecodeEventPublisherDeclType(t.Name, out var subKind, out var subId))
                    return null;
                object? attr;
                IList<CustomAttributeData> data;
                try
                {
                    attr = m.GetCustomAttributes(inherit: false)
                        .FirstOrDefault(a => a.GetType().Name == "NavEventSubscriberAttribute");
                    data = CustomAttributeData.GetCustomAttributes(m);
                }
                catch { return null; }
                if (attr == null || !TryReadAttribute(attr, out int objType, out int pubId, out string eventName))
                    return null;

                var procName = data.FirstOrDefault(d => d.AttributeType.Name == "NavNameAttribute")
                    ?.ConstructorArguments.FirstOrDefault().Value as string ?? m.Name;
                var args = data.FirstOrDefault(d => d.AttributeType.Name == "NavEventSubscriberAttribute")
                    ?.ConstructorArguments.Select(a => a.Value?.ToString() ?? "") ?? Array.Empty<string>();
                var manual = BcRuntime.IsManualBindingCodeunitType(t);
                var eventKey = BindingEventKey(objType, pubId, eventName);
                result.Add(new SubscriberBinding(
                    AlEventRaiseTracker.NormalizeDispatchKind(subKind), subId, procName, eventKey,
                    $"{t.Name}.{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})"
                    + $"|{string.Join(",", args)}|manual={manual}"));
            }
        }
        return result;
    }

    /// <summary>The key a raise of the bound event is recorded under: table trigger events
    /// (ordinals 1-10) are recorded per table, page trigger events per page, everything else per
    /// event. An object type the dispatcher does not know gets an <c>other|</c> key, which no
    /// raise ever records.</summary>
    internal static string BindingEventKey(int objectTypeOrdinal, int publisherId, string eventName)
    {
        var kind = AlEventRaiseTracker.KindOfObjectType(objectTypeOrdinal);
        if (kind == null) return $"other|{objectTypeOrdinal}|{publisherId}|{eventName}";
        if (kind == "Table" && ResolveEventOrdinalFromName(eventName) != 0)
            return AlEventRaiseTracker.TriggerKey(kind, publisherId);
        if (kind == "Page" && ResolvePageEventOrdinalFromName(eventName) != 0)
            return AlEventRaiseTracker.TriggerKey(kind, publisherId);
        return AlEventRaiseTracker.EventKey(kind, publisherId, eventName);
    }
}
