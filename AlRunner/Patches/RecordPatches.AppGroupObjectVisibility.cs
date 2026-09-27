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
    // (normalized kind, id) -> every app group whose source declares it.
    private static readonly Dictionary<(string Kind, int Id), HashSet<Guid>> _sourceObjectDeclarers = new();
    // (normalized kind, app group, id) -> that group's own parsed declaration and declared Caption.
    // The per-kind _parsed* dictionaries keep whichever group parsed an id last (#4767).
    private static readonly Dictionary<(string Kind, Guid AppGroup, int Id), object> _parsedByAppGroup = new();
    private static readonly Dictionary<(string Kind, Guid AppGroup, int Id), string?> _captionByAppGroup = new();
    // Every app group that declares at least one id another group also declares.
    private static readonly HashSet<Guid> _appGroupsSharingAnId = new();
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
            RecordAppGroupDependencyRefs(appId, identity.Dependencies);
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
        lock (_sourceOwnerApps) _sourceOwnerApps.Add(appId);
        foreach (var obj in ParseAlObjects(text))
        {
            if (AlObjectKindName(obj) is not string kind) continue;
            if (ObjectIdOf(obj) is not int id || id <= 0) continue;
            var key = (NormalizeObjectTypeName(kind), id);
            RecordObjectOwner(_sourceObjectOwners, _ambiguousSourceObjects, key, appId);
            if (!_sourceObjectDeclarers.TryGetValue(key, out var declarers))
                _sourceObjectDeclarers[key] = declarers = new HashSet<Guid>();
            declarers.Add(appId);
            if (declarers.Count > 1) _appGroupsSharingAnId.UnionWith(declarers);
            // Runs after every extractor has parsed this file, so the dictionaries hold THIS
            // file's declaration of the id right now.
            if (ParsedDeclarationOf(kind, id) is { } parsed) _parsedByAppGroup[(key.Item1, appId, id)] = parsed;
            _captionByAppGroup[(key.Item1, appId, id)] =
                _parsedObjectCaptions.TryGetValue((kind, id), out var caption) ? caption : null;
        }
    }

    private static object? ParsedDeclarationOf(string kind, int id) => NormalizeObjectTypeName(kind) switch
    {
        "table" => _parsedTables.GetValueOrDefault(id),
        "page" => _parsedPages.GetValueOrDefault(id),
        "pageextension" => _parsedPageExtensions.GetValueOrDefault(id),
        "report" => _parsedReports.GetValueOrDefault(id),
        "reportextension" => _parsedReportExtensions.GetValueOrDefault(id),
        "query" => _parsedQueries.GetValueOrDefault(id),
        "xmlport" => _parsedXmlPorts.GetValueOrDefault(id),
        _ => _parsedObjectDecls.GetValueOrDefault((kind, id)),
    };

    /// <summary>
    /// <paramref name="processWide"/>, or the executing app group's own declaration of
    /// (<paramref name="kind"/>, <paramref name="id"/>) when that group is one of several
    /// declaring the id (#4767).
    /// </summary>
    private static T InAppGroupScope<T>(string kind, int id, T processWide) where T : class
        => _appGroupsSharingAnId.Count > 0
           && AppGroupScopeFor(kind, id) is { } group
           && _parsedByAppGroup.TryGetValue((NormalizeObjectTypeName(kind), group, id), out var own)
           && own is T mine
            ? mine
            : processWide;

    /// <summary><paramref name="parsed"/>'s entry for <paramref name="id"/>, through
    /// <see cref="InAppGroupScope{T}(string, int, T)"/>: the lookup every runtime reader of a
    /// per-kind parsed dictionary uses (#4767).</summary>
    private static bool TryGetInAppGroupScope<T>(string kind, Dictionary<int, T> parsed, int id, out T value)
        where T : class
    {
        if (!parsed.TryGetValue(id, out value!)) return false;
        value = InAppGroupScope(kind, id, value);
        return true;
    }

    /// <summary>Every value of one per-kind parsed dictionary, each through
    /// <see cref="InAppGroupScope{T}(string, int, T)"/>.</summary>
    private static IEnumerable<T> InAppGroupScope<T>(string kind, Dictionary<int, T> parsed) where T : class
    {
        foreach (var (id, value) in parsed)
            yield return InAppGroupScope(kind, id, value);
    }

    /// <summary>The declared Caption of a shared id as the executing app group declares it;
    /// false when the id is not shared by that group.</summary>
    private static bool TryGetAppGroupCaption(string kind, int id, out string? caption)
    {
        caption = null;
        return _appGroupsSharingAnId.Count > 0
               && AppGroupScopeFor(kind, id) is { } group
               && _captionByAppGroup.TryGetValue((NormalizeObjectTypeName(kind), group, id), out caption);
    }

    /// <summary>
    /// A term for a per-process row cache built from the per-kind parsed dictionaries: the
    /// executing app group when it shares an id with another group, else Guid.Empty, so only
    /// such a group rebuilds the cache for itself (#4767).
    /// </summary>
    private static Guid AppGroupScopeKey()
        => _appGroupsSharingAnId.Count > 0 && CurrentAppGroupAppId() is { } g && _appGroupsSharingAnId.Contains(g)
            ? g
            : Guid.Empty;

    /// <summary>
    /// The executing app group, when it is one of SEVERAL source app groups declaring
    /// (<paramref name="kind"/>, <paramref name="id"/>); otherwise null. A per-id metadata cache
    /// consults this so each such group gets its own object instead of whichever group resolved
    /// the id first (#4751).
    /// </summary>
    internal static Guid? AppGroupScopeFor(string kind, int id)
        => AppGroupScopeFor(kind, id, _sourceObjectDeclarers, CurrentAppGroupAppId());

    /// <summary>
    /// The cache-key term for a runtime metadata object or emit-captured document of
    /// (<paramref name="kind"/>, <paramref name="id"/>): <see cref="AppGroupScopeFor(string, int)"/>,
    /// or Guid.Empty for every other caller, which keeps sharing the one process-wide entry (#4767).
    /// Reached on every record operation, so a run where no two groups share an id answers from
    /// the count check alone.
    /// </summary>
    internal static Guid AppGroupCacheScope(string kind, int id)
        => _appGroupsSharingAnId.Count > 0 && AppGroupScopeFor(kind, id) is { } group ? group : Guid.Empty;

    /// <summary>
    /// <paramref name="processWide"/>.GetOrAdd, except that an executing app group sharing the id
    /// with another group gets its own entry in <paramref name="byAppGroup"/>, built while that
    /// group executes (#4767). Clear both wherever the process-wide cache is cleared.
    /// </summary>
    internal static TValue GetOrAddInAppGroupScope<TValue>(
        System.Collections.Concurrent.ConcurrentDictionary<int, TValue> processWide,
        System.Collections.Concurrent.ConcurrentDictionary<(Guid AppGroup, int Id), TValue> byAppGroup,
        string kind, int id, Func<int, TValue> factory)
        => AppGroupCacheScope(kind, id) is var group && group != Guid.Empty
            ? byAppGroup.GetOrAdd((group, id), k => factory(k.Id))
            : processWide.GetOrAdd(id, factory);

    internal static Guid? AppGroupScopeFor(string kind, int id,
        IReadOnlyDictionary<(string Kind, int Id), HashSet<Guid>> declarers, Guid? executing)
        => executing is { } g
           && declarers.TryGetValue((NormalizeObjectTypeName(kind), id), out var d)
           && d.Count > 1 && d.Contains(g)
            ? g
            : null;

    private static void ResetAppGroupObjectVisibilityForReload()
    {
        _sourceObjectOwners.Clear();
        _ambiguousSourceObjects.Clear();
        _sourceObjectDeclarers.Clear();
        _parsedByAppGroup.Clear();
        _captionByAppGroup.Clear();
        _appGroupsSharingAnId.Clear();
        _appGroupBySourceDir.Clear();
        _scopeAssembly = null;
        _scopeAppId = Guid.Empty;
        ResetPackageObjectVisibilityForReload();
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
    /// <paramref name="visibleApps"/>. An object with no recorded owner (a platform object, a
    /// Microsoft-floor or unclaimed precompiled package) is never hidden: dropping it would
    /// remove rows this change has no evidence about.
    /// </summary>
    internal static bool IsHiddenFromAppGroup(
        string kind, int id, HashSet<Guid>? visibleApps, IReadOnlyDictionary<(string Kind, int Id), Guid> owners)
        => visibleApps != null
           && owners.TryGetValue((NormalizeObjectTypeName(kind), id), out var owner)
           && !visibleApps.Contains(owner);

    // A source owner (or source ambiguity) decides first; only an object no source declares is
    // looked up among the claimed precompiled packages (#4448).
    private static bool IsHiddenFromCurrentAppGroup(string kind, int id, HashSet<Guid>? visibleApps)
    {
        if (visibleApps == null) return false;
        var key = (NormalizeObjectTypeName(kind), id);
        if (_sourceObjectOwners.ContainsKey(key) || _ambiguousSourceObjects.Contains(key))
            return IsHiddenFromAppGroup(kind, id, visibleApps, _sourceObjectOwners);
        return IsHiddenFromAppGroup(kind, id, visibleApps, CurrentPackageVisibility().Owners);
    }

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
            VisibleApps = current is { } id ? VisibleAppClosure(id, CurrentPackageVisibility().Dependencies) : null,
        });
        CheckInventoryScope(scope.AppId, current, table);
        return WidenForExecutingApps(scope.VisibleApps);
    }

    /// <summary>
    /// The executing app group's visible-app closure, with NO provider to pin it against —
    /// for a caller that rebuilds its answer from scratch on every call.
    ///
    /// <para><see cref="PinInventoryScope"/> exists to refuse a store populated under one app
    /// group and read under another, because the per-provider "already inserted" sets are
    /// add-only. A caller that allocates a fresh result each time has no such store, so there
    /// is nothing to pin and nothing to refuse: the current group is simply read now. Using
    /// PinInventoryScope there would pin the FIRST group's closure onto a process-wide object
    /// and then throw for every later group (#4447).</para>
    /// </summary>
    private static HashSet<Guid>? CurrentVisibleAppClosure()
        => WidenForExecutingApps(
            CurrentAppGroupAppId() is { } id ? VisibleAppClosure(id, CurrentPackageVisibility().Dependencies) : null);

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
