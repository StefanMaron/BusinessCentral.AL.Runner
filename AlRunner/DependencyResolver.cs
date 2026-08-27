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

namespace AlRunner;

public sealed class DependencyResolver
{
    private readonly IReadOnlyList<string> _cacheDirs;
    // All candidates per AppId — kept so the highest satisfying version can be chosen.
    private readonly Dictionary<Guid, List<(AppManifest Manifest, string Path)>> _byId = new();
    private readonly Dictionary<(string Name, string Publisher), List<(AppManifest Manifest, string Path)>>
        _byNamePub = new(NamePublisherComparer.Instance);
    private bool _indexed;
    private readonly List<string> _diagnostics = new();
    // Kept separate from _diagnostics because these are printed UNCONDITIONALLY, not just
    // under --verbose: a dependency that no loader tier can implement is a certain runtime
    // failure, and the whole point of #1689 is that the developer never saw it coming.
    private readonly List<string> _unservable = new();

    /// <summary>
    /// Problems detected while resolving that are NOT fatal but almost certainly wrong —
    /// currently: a symbols-only package outranking a code-bearing copy of the same
    /// package on version. Such a set compiles cleanly and then dies deep inside BC with
    /// "The object with ID 0 does not have a member with that ID", naming neither the
    /// package nor the directory. Surfaced by the caller so the cause is stated where it
    /// happens instead of being reconstructed by hand from the whole dependency set.
    /// </summary>
    public IReadOnlyList<string> Diagnostics => _diagnostics;

    /// <summary>
    /// Resolved dependencies that no loader tier can supply an implementation for (neither
    /// an R2R payload nor AL source, and not a Microsoft platform app served by the service
    /// tier). Unlike <see cref="Diagnostics"/> these are always surfaced — each one is a
    /// call that will end in "The object with ID 0 does not have a member with that ID".
    /// </summary>
    public IReadOnlyList<string> UnservableDependencies => _unservable;

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
            throw new AlRunner.Infrastructure.MissingDependencyException(
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

    private bool SelectBestVersion(
        DependencyRef dep,
        List<(AppManifest Manifest, string Path)> candidates,
        out (AppManifest Manifest, string Path) found,
        out string? nearMissVersions)
    {
        nearMissVersions = null;
        (AppManifest Manifest, string Path) best = default;

        // A workspace .alpackages normally holds the SYMBOL-ONLY dev package of System
        // Application / Base Application while the executable R2R package lives in the
        // provisioned package cache. Picking the symbol-only copy makes every codeunit in
        // that app unresolvable at runtime: NavCodeunitHandle_CreateTarget substitutes a
        // NoOpCodeunit for the system id range, so the first procedure call dies with the
        // cryptic "Function ID N was called. The object with ID 0 does not have a member
        // with that ID."
        //
        // Executability therefore ranks ABOVE version among candidates that already satisfy
        // the declared minimum (filtered below): a package that cannot execute is not a
        // valid runtime answer while one that can is available. Ranking version first and
        // using executability only to settle exact ties — as this did — meant a symbols-only
        // copy that happened to carry a HIGHER version silently shadowed the code-bearing
        // one and nothing downstream could run it.
        //
        // That was not hypothetical. The al-language corpus commits
        // .alpackages/System Application.app at v27.5.46862.48827, symbols-only. On the BC
        // 27.0 and 27.3 matrix legs the provisioned code-bearing app (v27.0.38460.53260 /
        // v27.3.44313.53267) sorts BELOW it, so `Codeunit "Temp Blob"` lost its body and
        // every CreateInStream/CreateOutStream — and every report dataset built on one —
        // failed with object-ID-0. The 27.5 and 28.x legs passed only because their
        // provisioned build happened to outrank 48827 on version.
        //
        // Minimum-version semantics are untouched: the filter below still excludes anything
        // under dep.Version, so this only reorders candidates that were already acceptable.
        var isExecutable = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool Executable(string path)
        {
            if (!isExecutable.TryGetValue(path, out var r))
                isExecutable[path] = r = AppLoader.IsR2R(path);
            return r;
        }

        foreach (var c in candidates)
        {
            if (c.Manifest.Version < dep.Version) continue;
            if (best.Manifest == null) { best = c; continue; }

            var candidateExecutable = Executable(c.Path);
            if (candidateExecutable != Executable(best.Path))
            {
                if (candidateExecutable) best = c;
                continue;
            }
            if (c.Manifest.Version > best.Manifest.Version) best = c;
        }

        if (best.Manifest != null)
        {
            // Ranking executability first means a code-bearing candidate can no longer be
            // shadowed by a higher symbols-only one. So reaching here with a symbols-only
            // winner means NO code-bearing copy satisfied dep.Version at all — the code-
            // bearing copies, if any, were excluded by the minimum-version filter. That is
            // the one case left that still ends in object-ID-0 at runtime, and the fix is
            // provisioning rather than resolution, so name the versions involved.
            //
            // Not fatal, and deliberately quiet for Microsoft platform apps: symbols-only is
            // legitimate for those, whose runtime can come from the service-tier DLLs (see
            // IsMicrosoftPlatformApp). Warning on them would fire on healthy runs and teach
            // readers to ignore this.
            // "Cannot execute" is NOT the same as "carries no implementation". A package with
            // no publishedartifacts DLL but WITH src/*.al is served by the loader's Tier-3
            // on-the-fly source compile — which is exactly how Microsoft ships its test
            // toolkit. Verified against the real 28.1.49838.53479 test-apps artifact:
            // `Microsoft_Library Assert.app` is 22 KB, IsR2R=false, one src/*.al. Gating on
            // !Executable alone would fire on every healthy toolkit resolution, which is the
            // answer to the open question this diagnostic carried (#1689): the healthy run
            // tolerates a symbols-only winner because that winner still ships AL.
            //
            // The genuinely unservable shape is neither R2R nor AL — symbols and a manifest
            // and nothing else, as produced by a symbol-only package download. No loader tier
            // can implement it, so every call into it ends at
            // "The object with ID 0 does not have a member with that ID".
            if (!Executable(best.Path)
                && !AppLoader.HasAlSource(best.Path)
                && !IsMicrosoftPlatformApp(best.Manifest.Name, best.Manifest.Publisher))
            {
                var tooOld = candidates
                    .Where(c => c.Manifest.Version < dep.Version && Executable(c.Path))
                    .OrderByDescending(c => c.Manifest.Version)
                    .ToList();
                if (tooOld.Count == 0)
                {
                    // No other copy exists at all — the case #1689 reported, and the one this
                    // block used to fall straight through in silence. Nothing downstream can
                    // name the app: DependencyLoader's symbol-only branch says it is "relying
                    // on service-tier/already-loaded assembly" (true for platform apps, false
                    // here), and the runtime error that follows names neither app nor codeunit.
                    _unservable.Add(
                        $"[dep] {best.Manifest.Publisher}/{best.Manifest.Name} v{best.Manifest.Version} "
                        + "resolved to a package with NO IMPLEMENTATION (no publishedartifacts DLL,"
                        + "\n      no src/*.al) and no other copy was found in the package caches:"
                        + $"\n      winner: {best.Path}"
                        + "\n      Calls into this app will fail with \"The object with ID 0 does not"
                        + "\n      have a member with that ID\". Provision a package that carries an"
                        + "\n      implementation — `al-runner provision`, or re-run with --auto-provision;"
                        + "\n      for the Microsoft test toolkit specifically:"
                        + $"\n        al-runner provision --test-apps --bc-version {best.Manifest.Version}");
                }
                else
                    _diagnostics.Add(
                        $"[dep] note: {best.Manifest.Publisher}/{best.Manifest.Name} resolved to a "
                        + $"SYMBOLS-ONLY package v{best.Manifest.Version} (no publishedartifacts DLL):"
                        + $"\n           winner: {best.Path}"
                        + string.Concat(tooOld.Select(c =>
                            $"\n      below min: v{c.Manifest.Version} {c.Path} (code-bearing)"))
                        + $"\n           Code-bearing copies exist but are all below the required minimum"
                        + $"\n           v{dep.Version}, so none could be chosen. Provision a code-bearing"
                        + "\n           package at or above that version, or execution will fail with"
                        + "\n           \"The object with ID 0 does not have a member with that ID\".");
            }

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
