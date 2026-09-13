// RecordPatches.AppGroupObjectVisibility — which source-parsed objects the EXECUTING app group
// may see in the object-inventory virtual tables (AllObj, AllObjWithCaption, Table Metadata).
// See docs/virtual-tables-allobj.md#app-group-visibility (#2279).
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    // (normalized kind, id) -> the app group that compiled the file declaring it. An id two
    // different app groups declare is in _ambiguousSourceObjects instead and is never hidden.
    private static readonly Dictionary<(string Kind, int Id), Guid> _sourceObjectOwners = new();
    private static readonly HashSet<(string Kind, int Id)> _ambiguousSourceObjects = new();
    // App group id -> the app ids its app.json declares as dependencies (implicit floors excluded).
    private static readonly Dictionary<Guid, Guid[]> _sourceAppDependencies = new();
    // Full source dir -> the app group that compiles it; Guid.Empty when two groups share the dir
    // or the group has no app id. Not the nearest app.json: a suite compiles a sub-folder carrying
    // its own app.json into itself (CollectSuitePaths).
    private static readonly Dictionary<string, Guid> _appGroupBySourceDir = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Record that <paramref name="dirs"/> compile into the app group rooted at
    /// <paramref name="suiteDir"/>. Call before <see cref="AddSourceDirs"/> parses them.
    /// </summary>
    internal static void RegisterAppGroupSourceDirs(string suiteDir, IEnumerable<string> dirs)
    {
        var identity = InProcessAppPackager.ReadIdentity(Path.Combine(suiteDir, "app.json"));
        var appId = identity?.AppId ?? Guid.Empty;
        if (identity != null && appId != Guid.Empty)
            _sourceAppDependencies[appId] = identity.Dependencies
                .Select(d => d.AppId).Where(id => id != Guid.Empty).Distinct().ToArray();
        foreach (var dir in dirs)
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
            _appGroupBySourceDir[full] = _appGroupBySourceDir.TryGetValue(full, out var existing) && existing != appId
                ? Guid.Empty
                : appId;
        }
    }

    /// <summary>
    /// The app group owning <paramref name="filePath"/>: the longest registered source dir
    /// containing it. Guid.Empty when none is registered, or the dir is shared or unidentified.
    /// </summary>
    internal static Guid AppGroupOwningFile(string filePath, IReadOnlyDictionary<string, Guid> ownerByDir)
    {
        var bestLength = -1;
        var owner = Guid.Empty;
        var full = Path.GetFullPath(filePath);
        foreach (var (dir, appId) in ownerByDir)
        {
            if (dir.Length <= bestLength) continue;
            if (full.Length > dir.Length
                && full.StartsWith(dir, StringComparison.OrdinalIgnoreCase)
                && (full[dir.Length] == Path.DirectorySeparatorChar || full[dir.Length] == Path.AltDirectorySeparatorChar))
            {
                bestLength = dir.Length;
                owner = appId;
            }
        }
        return owner;
    }

    /// <summary>
    /// Record <paramref name="appId"/> as the owner of one object, unless a DIFFERENT app already
    /// claimed that (kind, id): then the object has no single owner and is never hidden.
    /// </summary>
    internal static void RecordObjectOwner(
        Dictionary<(string Kind, int Id), Guid> owners, HashSet<(string Kind, int Id)> ambiguous,
        (string Kind, int Id) key, Guid appId)
    {
        if (ambiguous.Contains(key)) return;
        if (owners.TryGetValue(key, out var existing) && existing != appId)
        {
            owners.Remove(key);
            ambiguous.Add(key);
            return;
        }
        owners[key] = appId;
    }

    private static void RecordSourceObjectOwners(string text, string filePath)
    {
        var appId = AppGroupOwningFile(filePath, _appGroupBySourceDir);
        if (appId == Guid.Empty) return;
        foreach (var obj in ParseAlObjects(text))
        {
            if (AlObjectKindName(obj) is not string kind) continue;
            if (ObjectIdOf(obj) is not int id || id <= 0) continue;
            RecordObjectOwner(_sourceObjectOwners, _ambiguousSourceObjects, (NormalizeObjectTypeName(kind), id), appId);
        }
    }

    private static void ResetAppGroupObjectVisibilityForReload()
    {
        _sourceObjectOwners.Clear();
        _ambiguousSourceObjects.Clear();
        _sourceAppDependencies.Clear();
        _appGroupBySourceDir.Clear();
        _scopeAssembly = null;
        _scopeAppId = Guid.Empty;
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

    private sealed class ProviderScope
    {
        public Guid? AppId;
        public HashSet<Guid>? VisibleApps;
    }
    private static readonly ConditionalWeakTable<object, ProviderScope> _inventoryScopeByProvider = new();
    // Last successful CurrentTestAssembly -> app id lookup; populators run on every data-access handout.
    private static System.Reflection.Assembly? _scopeAssembly;
    private static Guid _scopeAppId;

    /// <summary>
    /// The executing app group's app id, pinned to <paramref name="provider"/> on first use. The
    /// per-provider "already inserted" sets are add-only, so a store populated for one app group
    /// and handed out under another would keep the first group's rows: that refuses instead.
    /// </summary>
    private static HashSet<Guid>? PinInventoryScope(object provider, string table)
    {
        var current = CurrentAppGroupAppId();
        var scope = _inventoryScopeByProvider.GetValue(provider, _ => new ProviderScope
        {
            AppId = current,
            VisibleApps = current is { } id ? VisibleAppClosure(id, _sourceAppDependencies) : null,
        });
        CheckInventoryScope(scope.AppId, current, table);
        return scope.VisibleApps;
    }

    private static Guid? CurrentAppGroupAppId()
    {
        var asm = AlRunner.BcRuntime.CurrentTestAssembly;
        if (asm == null) return null;
        if (ReferenceEquals(asm, _scopeAssembly)) return _scopeAppId;
        foreach (var (registered, appId) in AlRunner.BcRuntime.RegisteredModuleAssemblies())
            if (ReferenceEquals(registered, asm))
            {
                (_scopeAssembly, _scopeAppId) = (asm, appId);
                return appId;
            }
        return null;
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
