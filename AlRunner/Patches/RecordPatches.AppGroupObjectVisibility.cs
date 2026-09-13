// RecordPatches.AppGroupObjectVisibility — which source-parsed objects the EXECUTING app group
// may see in the object-inventory virtual tables (AllObj, AllObjWithCaption, Table Metadata).
// See docs/virtual-tables-allobj.md#app-group-visibility (#2279).
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    // (normalized kind, id) -> the app whose app.json owns the .al file declaring it. Source-parsed
    // objects only: a precompiled dependency .app never reaches ParseSourceFileIntoAllExtractors.
    private static readonly Dictionary<(string Kind, int Id), Guid> _sourceObjectOwners = new();
    // Source app id -> the app ids its app.json declares as dependencies (implicit floors excluded).
    private static readonly Dictionary<Guid, Guid[]> _sourceAppDependencies = new();
    private static readonly Dictionary<string, string?> _owningManifestByDir = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Record which app declares every object in one source file, and that app's declared
    /// dependencies. A file under no readable app.json records nothing, so its objects stay
    /// visible everywhere, which is the behaviour before #2279.
    /// </summary>
    private static void RecordSourceObjectOwners(string text, string filePath)
    {
        var manifest = ResolveOwningManifest(filePath);
        if (manifest == null) return;
        var identity = InProcessAppPackager.ReadIdentity(manifest);
        if (identity == null || identity.AppId == Guid.Empty) return;

        _sourceAppDependencies[identity.AppId] = identity.Dependencies
            .Select(d => d.AppId).Where(id => id != Guid.Empty).Distinct().ToArray();

        foreach (var obj in ParseAlObjects(text))
        {
            if (AlObjectKindName(obj) is not string kind) continue;
            if (ObjectIdOf(obj) is not int id || id <= 0) continue;
            _sourceObjectOwners[(NormalizeObjectTypeName(kind), id)] = identity.AppId;
        }
    }

    private static string? ResolveOwningManifest(string filePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (dir == null) return null;
        if (_owningManifestByDir.TryGetValue(dir, out var memo)) return memo;
        string? found = null;
        for (var probe = dir; probe != null; probe = Path.GetDirectoryName(probe))
        {
            var candidate = Path.Combine(probe, "app.json");
            if (File.Exists(candidate)) { found = candidate; break; }
        }
        _owningManifestByDir[dir] = found;
        return found;
    }

    private static void ResetAppGroupObjectVisibilityForReload()
    {
        _sourceObjectOwners.Clear();
        _sourceAppDependencies.Clear();
        _owningManifestByDir.Clear();
    }

    /// <summary>
    /// <paramref name="appId"/> plus every app its app.json declares as a dependency,
    /// transitively through other source apps.
    /// </summary>
    internal static HashSet<Guid> VisibleAppClosure(Guid appId, IReadOnlyDictionary<Guid, Guid[]> dependencies)
    {
        var visible = new HashSet<Guid> { appId };
        var pending = new Stack<Guid>();
        pending.Push(appId);
        while (pending.Count > 0)
            if (dependencies.TryGetValue(pending.Pop(), out var deps))
                foreach (var dep in deps)
                    if (visible.Add(dep)) pending.Push(dep);
        return visible;
    }

    /// <summary>
    /// True only when the object's declaring source app is KNOWN and outside
    /// <paramref name="visibleApps"/>. An object with no recorded owner (a precompiled
    /// dependency, a platform object) is never hidden: dropping it would remove rows this
    /// change has no evidence about.
    /// </summary>
    internal static bool IsHiddenFromAppGroup(
        string kind, int id, HashSet<Guid>? visibleApps, IReadOnlyDictionary<(string Kind, int Id), Guid> owners)
        => visibleApps != null
           && owners.TryGetValue((NormalizeObjectTypeName(kind), id), out var owner)
           && !visibleApps.Contains(owner);

    private static bool IsHiddenFromCurrentAppGroup(string kind, int id, HashSet<Guid>? visibleApps)
        => IsHiddenFromAppGroup(kind, id, visibleApps, _sourceObjectOwners);

    private sealed class ProviderScope { public Guid? AppId; }
    private static readonly ConditionalWeakTable<object, ProviderScope> _inventoryScopeByProvider = new();

    /// <summary>
    /// The executing app group's app id, pinned to <paramref name="provider"/> on first use. The
    /// per-provider "already inserted" sets are add-only, so a store populated for one app group
    /// and handed out under another would keep the first group's rows: that refuses instead.
    /// </summary>
    private static HashSet<Guid>? PinInventoryScope(object provider, string table)
    {
        var asm = AlRunner.BcRuntime.CurrentTestAssembly;
        Guid? current = null;
        if (asm != null)
            foreach (var (registered, appId) in AlRunner.BcRuntime.RegisteredModuleAssemblies())
                if (ReferenceEquals(registered, asm)) { current = appId; break; }

        var scope = _inventoryScopeByProvider.GetValue(provider, _ => new ProviderScope { AppId = current });
        CheckInventoryScope(scope.AppId, current, table);
        return current is { } id ? VisibleAppClosure(id, _sourceAppDependencies) : null;
    }

    internal static void CheckInventoryScope(Guid? pinned, Guid? current, string table)
    {
        if (pinned == current) return;
        throw VirtualTableShapeGap(table, "app-group-visibility",
            $"the in-memory store was populated while app group {pinned?.ToString() ?? "<none>"} was executing "
            + $"and is now read by app group {current?.ToString() ?? "<none>"}; its rows cannot be removed, so "
            + "the second group would see the first group's objects — see AlRunner#2279");
    }
}
