using System.Reflection;

namespace AlRunner.Patches;

public static partial class EventSubscriberPatches
{
    /// <summary>
    /// The automatic subscribers an event raised by a dependency's Install trigger or by
    /// Company-Initialize can reach right now, as the sorted Module Version IDs of the assemblies
    /// declaring them (#5068). A term of <c>TestExecutor.CurrentInstallBaselineCacheKey</c>: those
    /// subscribers write rows into the baseline, so two runs that differ in them must not share it.
    /// </summary>
    /// <remarks>
    /// Manual-binding codeunits are left out: one fires only for an instance bound with
    /// BindSubscription, which takes a variable of that codeunit type, and dependency code is
    /// compiled without the assemblies that declare them. Leaving them in would make every edit
    /// of a test app with a manual subscriber recompute the baseline.
    /// Reads every subscriber registry: a registry added later belongs in the walk below.
    /// </remarks>
    internal static string AutomaticSubscriberAssemblyKey()
    {
        EnsureRegistryFresh();
        var types = new HashSet<Type>();
        lock (_lock)
        {
            foreach (var list in _byKey.Values) foreach (var h in list) types.Add(h.CodeunitType);
            foreach (var list in _byPageKey.Values) foreach (var h in list) types.Add(h.CodeunitType);
            foreach (var v in _validateSubs) types.Add(v.Handle.CodeunitType);
            foreach (var list in _byCodeunitKey.Values) AddDeclaringTypes(types, list);
            foreach (var list in _byTableEventKey.Values) AddDeclaringTypes(types, list);
            foreach (var list in _byObjectEventKey.Values) AddDeclaringTypes(types, list);
        }
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var t in types)
        {
            if (BcRuntime.IsManualBindingCodeunitType(t)) continue;
            var asm = t.Assembly;
            string id;
            try { id = asm.ManifestModule.ModuleVersionId.ToString("N"); }
            catch (Exception) when (asm.IsDynamic) { id = "dynamic:" + asm.FullName; }
            ids.Add(id);
        }
        return string.Join("|", ids);
    }

    private static void AddDeclaringTypes(HashSet<Type> types, List<MethodInfo> methods)
    {
        foreach (var m in methods)
            if (m.DeclaringType is { } t) types.Add(t);
    }
}
