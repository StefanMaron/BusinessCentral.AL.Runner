// RecordPatches.PackageObjectVisibility — owners for the objects of registered precompiled .app
// packages, so the app-group visibility filter can hide a package only a sibling group declares.
// CLAIM: the runner model of #2279 (each app group is its own tenant holding its dependency
// closure), extended to precompiled packages; not measured BC behaviour, which is tenant-wide.
// Anything this cannot place (floor, unclaimed, shared id) stays listed, as before.
// See docs/virtual-tables-allobj.md#precompiled-package-visibility (#4448).
namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>One registered .app: its identity, its manifest dependencies and the objects its
    /// SymbolReference.json declares.</summary>
    internal sealed record RegisteredPackage(
        Guid AppId, string Name, string Publisher,
        IReadOnlyList<DependencyRef> Dependencies, IReadOnlyList<(string Kind, int Id)> Objects);

    /// <summary>
    /// <c>Dependencies</c>: app id -> resolved dependency ids, over app groups AND packages, for
    /// <see cref="VisibleAppClosure"/>. <c>Owners</c>: (normalized kind, id) -> owning package, only
    /// for packages some app group claims and the Microsoft floor does not supply.
    /// </summary>
    internal sealed record PackageVisibility(
        IReadOnlyDictionary<Guid, Guid[]> Dependencies,
        IReadOnlyDictionary<(string Kind, int Id), Guid> Owners)
    {
        /// <summary>Every app that owns a hideable package object.</summary>
        public HashSet<Guid> OwnerApps { get; } = Owners.Values.ToHashSet();
    }

    // Installed in every tenant: `platform` resolves to Microsoft/System, `application` to the
    // Microsoft/Application umbrella (Base Application, System Application, Business Foundation;
    // ProgramSupport/Dependencies.cs). Named directly too, so a package cache lacking the umbrella
    // does not turn System Application into a hideable package.
    private static readonly string[] MicrosoftFloorAppNames =
        { "Application", "System", "Base Application", "System Application", "Business Foundation" };

    /// <summary>
    /// Which package objects the app-group filter may hide, and the dependency graph closures are
    /// computed over. A package is hideable only when some app group's closure reaches it
    /// (it was registered FOR a group) and the Microsoft floor's closure does not; every other
    /// package object keeps no owner and is never hidden, as before (#4455's platform control).
    /// A dependency resolves by app id, else by (name, publisher) — DependencyResolver.TryFind's order.
    /// </summary>
    internal static PackageVisibility BuildPackageVisibility(
        IReadOnlyList<RegisteredPackage> packages,
        IReadOnlyDictionary<Guid, DependencyRef[]> groupDependencies)
    {
        var packageIds = packages.Select(p => p.AppId).ToHashSet();

        IEnumerable<Guid> Resolve(DependencyRef dep)
        {
            if (dep.AppId != Guid.Empty)
            {
                yield return dep.AppId;
                if (packageIds.Contains(dep.AppId) || groupDependencies.ContainsKey(dep.AppId)) yield break;
            }
            foreach (var p in packages)
                if (string.Equals(p.Name, dep.Name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(p.Publisher, dep.Publisher, StringComparison.OrdinalIgnoreCase))
                    yield return p.AppId;
        }

        var graph = new Dictionary<Guid, HashSet<Guid>>();
        void AddEdges(Guid from, IEnumerable<DependencyRef> deps)
        {
            if (!graph.TryGetValue(from, out var set)) graph[from] = set = new HashSet<Guid>();
            foreach (var dep in deps)
                foreach (var to in Resolve(dep))
                    if (to != from) set.Add(to);
        }
        foreach (var (group, deps) in groupDependencies) AddEdges(group, deps);
        foreach (var p in packages) AddEdges(p.AppId, p.Dependencies);
        var dependencies = graph.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());

        var floor = new HashSet<Guid>();
        foreach (var p in packages)
            if (string.Equals(p.Publisher, "Microsoft", StringComparison.OrdinalIgnoreCase)
                && MicrosoftFloorAppNames.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
                floor.UnionWith(VisibleAppClosure(p.AppId, dependencies));

        var claimed = new HashSet<Guid>();
        foreach (var group in groupDependencies.Keys)
            claimed.UnionWith(VisibleAppClosure(group, dependencies));
        claimed.IntersectWith(packageIds);
        claimed.ExceptWith(floor);

        // Every package takes part in the ambiguity check, hideable or not: an id two packages
        // declare has no single owner, exactly as for source objects.
        var owners = new Dictionary<(string Kind, int Id), Guid>();
        var ambiguous = new HashSet<(string Kind, int Id)>();
        foreach (var p in packages)
            foreach (var (kind, id) in p.Objects)
                if (id > 0)
                    RecordObjectOwner(owners, ambiguous, (NormalizeObjectTypeName(kind), id), p.AppId);

        return new PackageVisibility(
            dependencies,
            owners.Where(kv => claimed.Contains(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    // App group id -> its app.json dependencies, implicit floors included (they resolve by name).
    private static readonly Dictionary<Guid, DependencyRef[]> _sourceAppDependencyRefs = new();
    private static int _appGroupRegistrationGeneration;
    private static readonly object _packageVisibilityLock = new();
    // A reference, so the unlocked fast-path read below cannot tear.
    private sealed record PackageVisibilityMemo(int Epoch, int Generation, PackageVisibility Model);
    private static volatile PackageVisibilityMemo? _packageVisibility;

    /// <summary>
    /// <see cref="BuildPackageVisibility"/> over what is registered now, rebuilt when either input
    /// moves: the .app set (<see cref="BcAppRegistrationEpoch"/>) or the app groups
    /// (<see cref="_appGroupRegistrationGeneration"/>).
    /// </summary>
    private static PackageVisibility CurrentPackageVisibility()
    {
        var epoch = BcAppRegistrationEpoch;
        var generation = System.Threading.Volatile.Read(ref _appGroupRegistrationGeneration);
        if (_packageVisibility is { } memo && memo.Epoch == epoch && memo.Generation == generation)
            return memo.Model;
        lock (_packageVisibilityLock)
        {
            if (_packageVisibility is { } again && again.Epoch == epoch && again.Generation == generation)
                return again.Model;
            var packages = new List<RegisteredPackage>();
            foreach (var (appPath, symbols) in EnumerateRegisteredBcAppSymbols("objects (app-group visibility)"))
            {
                // No stated app id: its objects stay unowned, as in BuildObjectOwnerIndex.
                if (!Guid.TryParse(symbols.AppId, out var appId) || appId == Guid.Empty) continue;
                var manifest = AlRunner.AppLoader.ReadManifest(appPath);
                var deps = manifest == null
                    ? new List<DependencyRef>()
                    : manifest.Dependencies.Concat(AlRunner.AppLoader.ImplicitRoots(manifest)).ToList();
                packages.Add(new RegisteredPackage(
                    appId, manifest?.Name ?? symbols.AppName ?? "", manifest?.Publisher ?? "", deps,
                    symbols.Objects.Select(o => (o.Kind, o.Id)).ToList()));
            }
            Dictionary<Guid, DependencyRef[]> groups;
            lock (_sourceAppDependencyRefs) groups = new Dictionary<Guid, DependencyRef[]>(_sourceAppDependencyRefs);
            var model = BuildPackageVisibility(packages, groups);
            _packageVisibility = new PackageVisibilityMemo(epoch, generation, model);
            return model;
        }
    }

    // A bundle dependency's install triggers and event subscribers fire under whichever app group
    // is executing, so that code must see its OWN closure: otherwise a read of its own table in
    // AllObj fails in a group that does not declare it (measured for both: EXEC-FAIL / FAIL
    // "cannot see its own table"). An awaited trigger resumes inside its own MoveNext, so its
    // assembly is on the stack whenever its code runs.
    /// <summary>
    /// <paramref name="visible"/> widened by the closure of every app whose code is executing, i.e.
    /// every registered AL assembly with a frame on the call stack. Returns
    /// <paramref name="visible"/> itself when nothing widens it. Rows a widened read inserts stay in
    /// that provider's add-only store, which is main's behaviour for them, never a hard failure.
    /// </summary>
    private static HashSet<Guid>? WidenForExecutingApps(HashSet<Guid>? visible)
    {
        if (visible == null) return null;
        var model = CurrentPackageVisibility();
        // The stack walk is the cost here; skip it when no owner exists that visible lacks. Three
        // hiding rules read visible: source owners, package owners, and the compiled-module owners
        // IsCompiledXmlPortOfUnreachableSourceApp reads — all three are checked.
        bool sourceOwnersVisible;
        lock (_sourceOwnerApps) sourceOwnersVisible = WideningCannotChangeAnAnswer(visible, _sourceOwnerApps);
        if (sourceOwnersVisible
            && WideningCannotChangeAnAnswer(visible, model.OwnerApps)
            && WideningCannotChangeAnAnswer(visible, AlRunner.BcRuntime.RegisteredModuleAssemblies().Select(m => m.AppId)))
            return visible;
        var executing = AlRunner.BcRuntime.AppIdsOnCallStack();
        HashSet<Guid>? widened = null;
        foreach (var app in executing)
            if (!visible.Contains(app))
                (widened ??= new HashSet<Guid>(visible))
                    .UnionWith(VisibleAppClosure(app, model.Dependencies));
        return widened ?? visible;
    }

    /// <summary>
    /// True when every app that could own a hidden object is already in <paramref name="visible"/>:
    /// IsHiddenFromAppGroup then answers false for every key, so no widening can change an answer.
    /// </summary>
    internal static bool WideningCannotChangeAnAnswer(HashSet<Guid> visible, IEnumerable<Guid> ownerApps)
        => ownerApps.All(visible.Contains);

    // Every app group that has been recorded as a source owner. A superset (an owner later made
    // ambiguous stays) only makes WidenForExecutingApps skip less often, never wrongly.
    private static readonly HashSet<Guid> _sourceOwnerApps = new();

    private static void RecordAppGroupDependencyRefs(Guid appId, IEnumerable<DependencyRef> dependencies)
    {
        lock (_sourceAppDependencyRefs) _sourceAppDependencyRefs[appId] = dependencies.ToArray();
        System.Threading.Interlocked.Increment(ref _appGroupRegistrationGeneration);
    }

    private static void ResetPackageObjectVisibilityForReload()
    {
        lock (_sourceOwnerApps) _sourceOwnerApps.Clear();
        lock (_sourceAppDependencyRefs) _sourceAppDependencyRefs.Clear();
        System.Threading.Interlocked.Increment(ref _appGroupRegistrationGeneration);
    }
}
