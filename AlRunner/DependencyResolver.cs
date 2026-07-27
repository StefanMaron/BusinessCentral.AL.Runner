// DependencyResolver — turns a bucket-level app.json dependency list +
// a set of package-cache dirs into a topologically-sorted list of (manifest, appPath).
//
// Indexes every `.app` under the cache dirs by AppId (with (Name, Publisher)
// as a fallback for declarations missing a GUID). All candidate versions are kept
// per AppId / (Name, Publisher). TryFind selects the highest-version candidate whose
// version satisfies the declared minimum (BC dep semantics: version is a minimum).
// If candidates exist but none satisfies the minimum the error message names the
// available versions so the failure is obviously a version-mismatch problem.
//
// Recursively expands declared deps via NavxManifest.xml's <Dependencies>. Detects
// cycles via colour-marker DFS. Output order = post-order DFS = topological order
// (deps before dependents).
//
// Throws on unresolved references with the requested name + version + the cache
// dirs that were searched, so the failure mode is obviously a missing-package
// problem and not a runner bug.

namespace AlRunnerV2;

public sealed class DependencyResolver
{
    private readonly IReadOnlyList<string> _cacheDirs;
    // All candidates per AppId — kept so the highest satisfying version can be chosen.
    private readonly Dictionary<Guid, List<(AppManifest Manifest, string Path)>> _byId = new();
    private readonly Dictionary<(string Name, string Publisher), List<(AppManifest Manifest, string Path)>>
        _byNamePub = new(NamePublisherComparer.Instance);
    private bool _indexed;

    public DependencyResolver(IReadOnlyList<string> cacheDirs)
    {
        _cacheDirs = cacheDirs;
    }

    /// <summary>
    /// Resolve a list of root deps (typically the bucket's app.json
    /// <c>dependencies</c>) and return the full transitive closure in
    /// topological order (deps before dependents).
    /// </summary>
    public IReadOnlyList<(AppManifest Manifest, string AppPath)> Resolve(
        IEnumerable<DependencyRef> roots)
    {
        EnsureIndexed();

        var visited = new Dictionary<Guid, byte>(); // 0 = unvisited, 1 = on-stack, 2 = done
        var result = new List<(AppManifest, string)>();

        foreach (var root in roots)
            Visit(root, visited, result, new Stack<string>());

        return result;
    }

    // Microsoft platform apps the runner provides via precompiled service-tier DLLs +
    // bundle .alpackages symbols, never by loading a resolved .app — so a missing .app is
    // expected and non-fatal. Kept in sync with Program.IsMicrosoftPlatformApp.
    internal static bool IsMicrosoftPlatformApp(string name, string publisher)
    {
        if (!string.Equals(publisher, "Microsoft", StringComparison.OrdinalIgnoreCase)) return false;
        return name is "Base Application" or "System Application" or "Business Foundation"
            or "Application" or "System";
    }

    private void Visit(
        DependencyRef dep,
        Dictionary<Guid, byte> state,
        List<(AppManifest, string)> output,
        Stack<string> stack)
    {
        if (!TryFind(dep, out var found, out var nearMissVersions))
        {
            if (dep.Optional || IsMicrosoftPlatformApp(dep.Name, dep.Publisher))
            {
                // Microsoft platform apps (Base Application / System Application / …) are
                // provided by the precompiled service-tier DLLs at runtime and the bundle
                // .alpackages symbols at compile time — never loaded from a resolved .app.
                // So a missing .app for them is expected (e.g. CI, where packageCacheDirs is
                // empty); skip rather than fail, including when reached transitively via a
                // dependent's manifest (this branch also fires for non-Optional manifest deps).
                Console.Error.WriteLine(
                    $"  [deps] dependency not found in cache, skipping: " +
                    $"{dep.Publisher}/{dep.Name}");
                return;
            }
            if (nearMissVersions != null)
            {
                // Dep IS in the cache, but every candidate is below the declared minimum version.
                // This is a version-mismatch problem, not a provisioning gap.
                throw new InvalidOperationException(
                    $"Dependency not found: {dep.Publisher}/{dep.Name} v{dep.Version} " +
                    $"(found same-named package at {nearMissVersions} — all below minimum v{dep.Version}). " +
                    $"Searched: {string.Join(", ", _cacheDirs)}. " +
                    $"Stack: {string.Join(" -> ", stack.Reverse())}");
            }
            // Dep is completely absent from every searched directory — this is a provisioning gap.
            // Throw MissingDependencyException (not InvalidOperationException) so Program.cs can
            // emit ONE loud, actionable "provisioning gap" message and abort before attempting a
            // doomed bundle compile that would produce thousands of misleading AL0185 errors.
            throw new AlRunnerV2.Infrastructure.MissingDependencyException(
                dep.Publisher, dep.Name, dep.Version.ToString(), dep.AppId,
                _cacheDirs.ToList(),
                stack.Count > 0
                    ? string.Join(" → ", stack.Reverse().Append(dep.Name))
                    : dep.Name);
        }

        var id = found.Manifest.AppId;
        if (state.TryGetValue(id, out var s))
        {
            if (s == 1)
                throw new InvalidOperationException(
                    $"Dependency cycle detected at {found.Manifest.Name}: " +
                    $"{string.Join(" -> ", stack.Reverse())} -> {found.Manifest.Name}");
            if (s == 2) return;
        }

        state[id] = 1;
        stack.Push(found.Manifest.Name);
        foreach (var child in found.Manifest.Dependencies)
            Visit(child, state, output, stack);
        stack.Pop();
        state[id] = 2;
        output.Add((found.Manifest, found.Path));
    }

    /// <summary>
    /// Find the best candidate for <paramref name="dep"/>, selecting the highest version
    /// that satisfies the declared minimum (BC minimum-version semantics).
    /// </summary>
    /// <param name="nearMissVersions">
    /// Set when candidates exist but none satisfies the minimum version; contains a
    /// human-readable summary of the available-but-too-low versions.
    /// </param>
    private bool TryFind(DependencyRef dep,
        out (AppManifest Manifest, string Path) found,
        out string? nearMissVersions)
    {
        nearMissVersions = null;

        // AppId lookup is authoritative when present. If candidates exist for this AppId
        // but none satisfies the minimum, we must NOT silently fall through to the
        // name+publisher index — that could silently pick a completely different package.
        if (dep.AppId != Guid.Empty && _byId.TryGetValue(dep.AppId, out var byIdCandidates))
            return SelectBestVersion(dep, byIdCandidates, out found, out nearMissVersions);

        // Name+Publisher fallback: used when AppId is empty, or when the AppId is not
        // in the index at all (nearMissVersions stays null in that path).
        if (_byNamePub.TryGetValue((dep.Name, dep.Publisher), out var byNameCandidates))
            return SelectBestVersion(dep, byNameCandidates, out found, out nearMissVersions);

        found = default;
        return false;
    }

    private static bool SelectBestVersion(
        DependencyRef dep,
        List<(AppManifest Manifest, string Path)> candidates,
        out (AppManifest Manifest, string Path) found,
        out string? nearMissVersions)
    {
        nearMissVersions = null;
        (AppManifest Manifest, string Path) best = default;

        foreach (var c in candidates)
        {
            if (c.Manifest.Version < dep.Version) continue;
            if (best.Manifest == null || c.Manifest.Version > best.Manifest.Version)
            {
                best = c;
                continue;
            }
            // Same version, two packages. Version alone cannot separate them, so the old
            // `>` comparison left the winner decided by index order — i.e. by which cache
            // dir happened to be scanned first. A workspace .alpackages normally holds the
            // SYMBOL-ONLY dev package of System Application / Base Application while the
            // executable R2R package lives in the provisioned package cache; picking the
            // symbol-only copy makes every codeunit in that app unresolvable at runtime,
            // and NavCodeunitHandle_CreateTarget then substitutes a NoOpCodeunit for the
            // system id range — so the first procedure call dies with the cryptic
            // "Function ID N was called. The object with ID 0 does not have a member with
            // that ID." Prefer the package that can actually execute. Version stays the
            // primary key (checked above), so this never overrides minimum-version
            // semantics — it only settles a tie.
            if (c.Manifest.Version == best.Manifest.Version
                && AppLoader.IsR2R(c.Path) && !AppLoader.IsR2R(best.Path))
            {
                best = c;
            }
        }

        if (best.Manifest != null)
        {
            found = best;
            return true;
        }

        // Candidates exist but all are below the required minimum.
        nearMissVersions = string.Join(", ",
            candidates.OrderByDescending(c => c.Manifest.Version).Select(c => $"v{c.Manifest.Version}"));
        found = default;
        return false;
    }

    private void EnsureIndexed()
    {
        if (_indexed) return;
        foreach (var dir in _cacheDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.app", SearchOption.AllDirectories))
            {
                var m = AppLoader.ReadManifest(file);
                if (m == null) continue;
                // Collect ALL candidates per AppId so version-aware selection can choose the best.
                if (!_byId.TryGetValue(m.AppId, out var idList))
                    _byId[m.AppId] = idList = new List<(AppManifest, string)>();
                idList.Add((m, file));

                var key = (m.Name, m.Publisher);
                if (!_byNamePub.TryGetValue(key, out var npList))
                    _byNamePub[key] = npList = new List<(AppManifest, string)>();
                npList.Add((m, file));
            }
        }
        _indexed = true;
    }

    private sealed class NamePublisherComparer : IEqualityComparer<(string Name, string Publisher)>
    {
        public static readonly NamePublisherComparer Instance = new();
        public bool Equals((string Name, string Publisher) x, (string Name, string Publisher) y)
            => StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name)
            && StringComparer.OrdinalIgnoreCase.Equals(x.Publisher, y.Publisher);
        public int GetHashCode((string Name, string Publisher) o)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(o.Name),
                StringComparer.OrdinalIgnoreCase.GetHashCode(o.Publisher));
    }
}
