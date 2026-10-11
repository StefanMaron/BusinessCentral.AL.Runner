// BcCompiler.PackageClosure (#5472) - which packages of the scan set the package loader is built over.
using AlRunner.Infrastructure;

namespace AlRunner;

public sealed partial class BcCompiler
{
    // BC's AbstractSymbolReferenceAnalyzer keeps package manifests in a 100-entry LRU and walks the
    // whole scan set per reference lookup: past this many packages every lookup re-reads every
    // manifest (CodeAnalysis 28.1.49838.53910, the analyzer's ctor: ldc.i4.s 100). Re-check on a BC bump.
    internal const int BcManifestCacheCapacity = 100;

    /// <summary>One identity a package may satisfy; matched by AppId or by name, a superset of
    /// SymbolReferenceSpecification.IsSatisfiedBy (AppId, Application by name, name + publisher).</summary>
    internal readonly record struct ClosureSeed(Guid AppId, string Name);

    // Monotonic for the process: a later bundle's smaller closure must not change the scan set,
    // or the loader built for the earlier one would be rebuilt for nothing.
    private static readonly HashSet<ClosureSeed> _closureSeeds = new();

    // Caller holds _refSync.
    private static void AddClosureSeeds(IReadOnlyList<(AppManifest Manifest, string AppPath)>? deps)
    {
        if (deps == null) return;
        foreach (var d in deps) _closureSeeds.Add(new ClosureSeed(d.Manifest.AppId, d.Manifest.Name));
    }

    /// <summary>The seeds to restrict the scan to, or null for the whole folder. Null whenever the
    /// run did not resolve its dependencies (the --precompile fallback adds every discoverable app
    /// as a spec), so only a run that names its closure ever gets a narrowed scan. Caller holds _refSync.</summary>
    private static IReadOnlyCollection<ClosureSeed>? CurrentClosureSeeds()
        => _resolvedDeps is { Count: > 0 } && !_usePackageCacheFallback && _closureSeeds.Count > 0
            ? _closureSeeds.ToArray()
            : null;

    /// <summary>
    /// The indexes of <paramref name="candidates"/> in the dependency closure of <paramref name="seeds"/>:
    /// everything a seed names, then what each kept package declares as a dependency or as its
    /// Application / Platform floor (<see cref="AppLoader.ImplicitRoots"/>). All versions of a kept
    /// identity are kept, so the version BC picks among them is the one it picked over the whole folder.
    /// </summary>
    internal static HashSet<int> ComputeClosure(
        IReadOnlyList<AppManifest> candidates, IEnumerable<ClosureSeed> seeds)
    {
        var byId = new Dictionary<Guid, List<int>>();
        var byName = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].AppId != Guid.Empty)
                (byId.TryGetValue(candidates[i].AppId, out var l1) ? l1 : byId[candidates[i].AppId] = new()).Add(i);
            (byName.TryGetValue(candidates[i].Name, out var l2) ? l2 : byName[candidates[i].Name] = new()).Add(i);
        }

        var kept = new HashSet<int>();
        var visited = new HashSet<ClosureSeed>();
        var queue = new Queue<ClosureSeed>(seeds);
        while (queue.Count > 0)
        {
            var seed = queue.Dequeue();
            if (!visited.Add(seed)) continue;
            var hits = new List<int>();
            if (seed.AppId != Guid.Empty && byId.TryGetValue(seed.AppId, out var ids)) hits.AddRange(ids);
            if (!string.IsNullOrEmpty(seed.Name) && byName.TryGetValue(seed.Name, out var names)) hits.AddRange(names);
            foreach (var i in hits)
            {
                if (!kept.Add(i)) continue;
                foreach (var dep in candidates[i].Dependencies) queue.Enqueue(new ClosureSeed(dep.AppId, dep.Name));
                foreach (var floor in AppLoader.ImplicitRoots(candidates[i])) queue.Enqueue(new ClosureSeed(floor.AppId, floor.Name));
            }
        }
        return kept;
    }
}
