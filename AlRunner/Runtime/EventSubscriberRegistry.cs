using System.Reflection;

namespace AlRunner.Runtime;

/// <summary>
/// Runtime index of <c>[NavEventSubscriber(ObjectType, N, "EventName", …)]</c>
/// methods found in the loaded assembly. Populated lazily per assembly
/// and consulted by <see cref="AlCompat.FireEvent"/> when a publisher's
/// rewritten event-method body fires, or by <see cref="MockRecordHandle"/>
/// for implicit DB trigger events.
/// </summary>
public static class EventSubscriberRegistry
{
    // BC ObjectType enum constants
    public const int ObjectTypeTable = 1;
    public const int ObjectTypeCodeunit = 5;

    // Subscriber index keyed by (ObjectType, ObjectId, EventName)
    private static readonly Dictionary<Assembly, Dictionary<(int ObjectType, int ObjectId, string EventName), List<SubscriberEntry>>> _byAssembly = new();

    // Manually-bound codeunit instances (for EventSubscriberInstance = Manual)
    private static readonly List<object> _boundInstances = new();

    // Codeunit types detected as Manual subscribers (populated during Build)
    private static readonly HashSet<Type> _manualSubscriberTypes = new();

    public record struct SubscriberEntry(Type OwnerType, MethodInfo Method);

    public static IReadOnlyList<SubscriberEntry> GetSubscribers(
        Assembly assembly, int objectType, int objectId, string eventName)
    {
        if (!_byAssembly.TryGetValue(assembly, out var map))
        {
            map = Build(assembly);
            _byAssembly[assembly] = map;
        }

        return map.TryGetValue((objectType, objectId, eventName), out var list)
            ? list
            : Array.Empty<SubscriberEntry>();
    }

    /// <summary>Backward-compat overload — defaults to ObjectType.Codeunit.</summary>
    public static IReadOnlyList<SubscriberEntry> GetSubscribers(
        Assembly assembly, int publisherCodeunitId, string eventName)
        => GetSubscribers(assembly, ObjectTypeCodeunit, publisherCodeunitId, eventName);

    /// <summary>
    /// Bind a manual subscriber instance. The instance's subscriber methods
    /// will be dispatched until <see cref="Unbind"/> is called.
    /// </summary>
    public static void Bind(object instance)
    {
        _boundInstances.Add(instance);
    }

    /// <summary>
    /// Unbind a previously-bound manual subscriber instance.
    /// </summary>
    public static void Unbind(object instance)
    {
        _boundInstances.Remove(instance);
    }

    /// <summary>
    /// Returns true if the given type is a manual subscriber (EventSubscriberInstance = Manual).
    /// </summary>
    public static bool IsManualSubscriber(Type type)
    {
        // Ensure the assembly is scanned
        var asm = type.Assembly;
        if (!_byAssembly.ContainsKey(asm))
        {
            _byAssembly[asm] = Build(asm);
        }
        return _manualSubscriberTypes.Contains(type);
    }

    /// <summary>
    /// Returns all currently-bound instances whose type matches the given type.
    /// </summary>
    public static IEnumerable<object> GetBoundInstances(Type subscriberType)
    {
        foreach (var inst in _boundInstances)
            if (inst.GetType() == subscriberType)
                yield return inst;
    }

    public static void Clear()
    {
        _byAssembly.Clear();
        _boundInstances.Clear();
        _manualSubscriberTypes.Clear();
    }

    /// <summary>
    /// Reset per-test state (bound instances) without clearing the assembly cache.
    /// </summary>
    public static void ResetBindings()
    {
        _boundInstances.Clear();
    }

    private static Dictionary<(int, int, string), List<SubscriberEntry>> Build(Assembly assembly)
    {
        var result = new Dictionary<(int, int, string), List<SubscriberEntry>>();

        // Detect manual subscriber types by checking for ManualEventSubscriber attribute
        // (emitted by the rewriter to preserve EventSubscriberInstance = Manual)
        var manualTypes = new HashSet<Type>();

        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

        foreach (var type in types)
        {
            if (type == null) continue;

            // Check if this type has the ManualEventSubscriber marker attribute
            foreach (var attr in type.GetCustomAttributesData())
            {
                if (attr.AttributeType.Name == "ManualEventSubscriberAttribute")
                {
                    manualTypes.Add(type);
                    _manualSubscriberTypes.Add(type);
                    break;
                }
            }

            foreach (var method in type.GetMethods(
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                foreach (var data in method.GetCustomAttributesData())
                {
                    if (data.AttributeType.Name != "NavEventSubscriberAttribute") continue;

                    // Expected positional order:
                    //   (ObjectType targetObjectType, int targetObjectNo,
                    //    string targetMethodName, [int memberId,]
                    //    string fieldName, EventSubscriberCallOptions callOptions)
                    int? objType = null;
                    int? objId = null;
                    string? evName = null;
                    int intSeen = 0;
                    foreach (var arg in data.ConstructorArguments)
                    {
                        if (arg.Value is int i)
                        {
                            intSeen++;
                            if (intSeen == 1) objType = i;
                            if (intSeen == 2 && objId is null) objId = i;
                            continue;
                        }
                        if (arg.Value is string s && evName is null) evName = s;
                    }

                    if (objType == null || objId == null || evName == null) continue;

                    var key = (objType.Value, objId.Value, evName);
                    if (!result.TryGetValue(key, out var list))
                        result[key] = list = new List<SubscriberEntry>();
                    list.Add(new SubscriberEntry(type, method));
                }
            }
        }

        return result;
    }
}
