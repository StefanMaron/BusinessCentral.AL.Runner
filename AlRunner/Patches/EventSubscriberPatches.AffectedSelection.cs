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
            var seedAssemblies = BcRuntime.StampedModuleAssemblies();
            // #3415 SPIKE (not for merge): AL_RUNNER_RECORD_ALL_APP_EVENTS=1 also seeds every
            // UNSTAMPED AL app assembly (Base/System Application, Business Foundation, test
            // libraries), so a raise of a Microsoft publisher with no subscriber is recorded too.
            if (Environment.GetEnvironmentVariable("AL_RUNNER_RECORD_ALL_APP_EVENTS") == "1")
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int before = _seededScopeTypes.Count;
                var extra = UnstampedAlAppAssemblies(seedAssemblies);
                foreach (var asm in extra)
                    if (!_recordingSeedByAssembly.ContainsKey(asm))
                        _recordingSeedByAssembly[asm] = SeedAssembly(asm, sentinel);
                Console.Error.WriteLine(
                    $"[spike-3415] stamped request modules ({seedAssemblies.Count}): "
                    + string.Join(", ", seedAssemblies.Select(a => a.GetName().Name)));
                Console.Error.WriteLine(
                    $"[spike-3415] seeded {_seededScopeTypes.Count - before} more event scopes in "
                    + $"{extra.Count} Microsoft/unstamped app assemblies ({string.Join(", ", extra.Select(a => a.GetName().Name))}) "
                    + $"in {sw.ElapsedMilliseconds} ms");
            }
            var swStamped = System.Diagnostics.Stopwatch.StartNew();
            foreach (var asm in seedAssemblies)
            {
                if (!_recordingSeedByAssembly.TryGetValue(asm, out var state)
                    || (!state.Seeded && sentinel != null))
                    _recordingSeedByAssembly[asm] = state = SeedAssembly(asm, sentinel);
                foreach (var o in state.Objects)
                    publishers[o] = (publishers.TryGetValue(o, out var s) ? s : true) && state.Seeded;
            }
            Console.Error.WriteLine($"[spike-3415] recording seed over stamped modules: {_seededScopeTypes.Count} scopes seeded in total, "
                + $"{publishers.Count} publisher objects, {swStamped.ElapsedMilliseconds} ms");

            var observable = new HashSet<string>(StringComparer.Ordinal);
            foreach (var scope in _seededScopeTypes)
            {
                if (BcRuntime.IsStaleBundleAssembly(scope.Assembly)) continue;
                if (AlEventRaiseTracker.EventScopeKey(scope) is { } k) observable.Add(k);
            }
            return new EventObservability(observable, publishers);
        }
    }

    // #3415 SPIKE: every loaded, non-stale assembly that references Ncl and is not a request
    // module — the precompiled Microsoft apps the runner loaded from .app packages.
    private static List<Assembly> UnstampedAlAppAssemblies(List<Assembly> stamped)
    {
        var stampedSet = new HashSet<Assembly>(stamped, ReferenceEqualityComparer.Instance);
        var result = new List<Assembly>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic || stampedSet.Contains(asm) || BcRuntime.IsStaleBundleAssembly(asm)) continue;
            var n = asm.GetName().Name ?? "";
            if (n.StartsWith("System.") || n.StartsWith("Microsoft.Extensions.") || n == "netstandard"
                || n == "mscorlib" || n == "AlRunner" || n == "Runner" || n.StartsWith("Microsoft.CodeAnalysis")
                || n == "al-runner" || n.StartsWith("Microsoft.AspNetCore")
                || n.StartsWith("Microsoft.Dynamics.Nav.Ncl") || n.StartsWith("Microsoft.Dynamics.Nav.Types")) continue;
            try
            {
                // An AL app assembly declares Codeunit<N>/Record<N>/Page<N> classes.
                var idx = AssemblyTypeIndex.For(asm);
                bool AlClass(string p) => idx.TypeNamesWithPrefix(p).Any(s => s.Length > p.Length && char.IsDigit(s[p.Length]));
                if (!AlClass("Codeunit") && !AlClass("Record") && !AlClass("Page")) continue;
            }
            catch { continue; }
            result.Add(asm);
        }
        return result;
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
            if (_seededScopeTypes.Contains(t)) continue;
            FieldInfo? fld;
            try { fld = EventScopeField(t); }
            catch (Exception ex)
            {
                // #3415 SPIKE: a non-AL assembly's nested *_Scope type whose field types do not load.
                complete = false;
                Console.Error.WriteLine($"[spike-3415] skip {t.FullName} in {asm.GetName().Name}: {ex.GetType().Name}");
                continue;
            }
            if (fld == null) continue; // an ordinary procedure scope
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
