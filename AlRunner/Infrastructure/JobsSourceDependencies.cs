// JobsSourceDependencies — the source folders a `--jobs` worker must COMPILE but must not RUN (#5267).
//
// A bundle can depend on another source folder of the same invocation. Without --jobs both are in
// one process, so the dependency is built into a package before the dependent loads. A worker
// handed only its own shard has no such package for a dependency the planner placed on another
// shard, and fails with a provisioning gap that names a package nobody can supply.
//
// Each worker is therefore handed, beside its own bundles, the listed folders those bundles depend
// on (directly or through other folders), marked dependency-only: they are compiled by the same
// pre-pass a plain run uses and are never run, so a dependency that has tests of its own reports
// them from the one worker that owns it as a bundle. docs/jobs-unit-claiming.md § Source dependencies.

namespace AlRunner.Infrastructure;

internal static class JobsSourceDependencies
{
    /// <summary>The folders of this worker's argument list that it compiles and does not run,
    /// `|`-separated. Unset = every folder is run, as without --jobs.</summary>
    public const string DependencyOnlyEnvVar = "AL_RUNNER_JOBS_DEPENDENCY_ONLY";

    /// <summary>
    /// The folders of <paramref name="allBundles"/> that <paramref name="shardBundles"/> depend on,
    /// directly or through other folders, and that the shard does not already hold, in
    /// <paramref name="allBundles"/>' order. A dependency that is not among the listed folders (a
    /// precompiled package) is not one of them, and a folder with no readable identity relates to
    /// nothing. Each folder appears once however many paths lead to it.
    /// </summary>
    public static List<string> OutsideShard(
        IReadOnlyCollection<string> shardBundles, IReadOnlyList<string> allBundles,
        Func<string, BundleIdentity?> identityOf)
    {
        var held = new HashSet<string>(shardBundles.Select(ParallelFanOut.Normalize), StringComparer.OrdinalIgnoreCase);
        var identities = new Dictionary<string, BundleIdentity?>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in allBundles) identities[ParallelFanOut.Normalize(b)] = identityOf(b);

        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var work = new Queue<string>(held);
        while (work.Count > 0)
        {
            var consumer = work.Dequeue();
            if (!identities.TryGetValue(consumer, out var mine) || mine == null) continue;
            foreach (var (path, other) in identities)
            {
                if (other == null || string.Equals(path, consumer, StringComparison.OrdinalIgnoreCase)) continue;
                if (!BundleDependencyOrder.DependsOn(mine, other)) continue;
                if (needed.Add(path)) work.Enqueue(path);
            }
        }
        needed.ExceptWith(held);
        return allBundles.Where(b => needed.Contains(ParallelFanOut.Normalize(b))).ToList();
    }

    /// <summary><see cref="OutsideShard"/> over the folders' app.json files, found the way the
    /// pre-pass finds them: the folder's own, else its bucket root's.</summary>
    public static List<string> OutsideShard(IReadOnlyCollection<string> shardBundles, IReadOnlyList<string> allBundles)
        => OutsideShard(shardBundles, allBundles, ReadIdentity);

    private static BundleIdentity? ReadIdentity(string bundle)
    {
        var abs = Path.GetFullPath(bundle);
        var appJson = Path.Combine(abs, "app.json");
        if (!File.Exists(appJson))
        {
            var root = WatchSource.FindBucketRoot(abs);
            if (root != null) appJson = Path.Combine(root, "app.json");
        }
        return File.Exists(appJson) ? InProcessAppPackager.ReadIdentity(appJson) : null;
    }

    public static string Format(IEnumerable<string> dependencyOnly)
        => string.Join("|", dependencyOnly.Select(ParallelFanOut.Normalize));

    /// <summary>The set <paramref name="value"/> names, as normalised full paths; empty when unset.</summary>
    public static HashSet<string> Parse(string? value)
        => new((value ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries).Select(ParallelFanOut.Normalize),
            StringComparer.OrdinalIgnoreCase);

    /// <summary><see cref="Parse"/> over this process's environment.</summary>
    public static HashSet<string> FromEnvironment()
        => Parse(Environment.GetEnvironmentVariable(DependencyOnlyEnvVar));

    /// <summary>The folders of <paramref name="bundles"/> this process runs: all of them unless the
    /// parent marked some dependency-only. The same list instance when none is, so an ordinary run
    /// is untouched.</summary>
    public static List<string> RunBundles(List<string> bundles, IReadOnlySet<string> dependencyOnly)
        => dependencyOnly.Count == 0
            ? bundles
            : bundles.Where(b => !dependencyOnly.Contains(ParallelFanOut.Normalize(b))).ToList();
}
