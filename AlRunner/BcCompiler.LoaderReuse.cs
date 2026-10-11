// BcCompiler.LoaderReuse (#5473) - when a bundle may keep the package loader a previous one built.
namespace AlRunner;

public sealed partial class BcCompiler
{
    /// <summary>A resolved dependency the package loader can serve: where it is, the key naming its
    /// bytes (<c>path@version@hash</c>), and whether the bytes could be named at all.</summary>
    internal readonly record struct ServedDep(string Path, string Key, bool Identified);

    internal enum LoaderReuse { Rebuild, Reuse, ReuseAndWarm }

    /// <summary>What one built package loader was built over, beyond its dep keys: the paths those keys
    /// name, the stat of every package in its scan, and the specs already walked through it.</summary>
    internal sealed class LoaderBuild
    {
        internal readonly HashSet<string> DepPaths = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, (long Length, long Ticks)> InventoryStat = new(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> WarmedSpecs = new(StringComparer.OrdinalIgnoreCase);

        internal static LoaderBuild For(IReadOnlyList<ServedDep> deps, IEnumerable<PackageScanEntry> scanInventory)
        {
            var build = new LoaderBuild();
            build.AddDeps(deps);
            foreach (var e in scanInventory)
                if (StatOf(e.Path) is { } st) build.InventoryStat[e.Path] = st;
            return build;
        }

        internal void AddDeps(IEnumerable<ServedDep> deps)
        {
            foreach (var d in deps) DepPaths.Add(d.Path);
        }
    }

    private static (long Length, long Ticks)? StatOf(string path)
    {
        try
        {
            var f = new FileInfo(path);
            return f.Exists ? (f.Length, f.LastWriteTimeUtc.Ticks) : null;
        }
        catch { return null; }
    }

    // The loader answers from a directory enumeration at lookup time and never sees the dependency
    // list (#1832), so a dependency this loader was not built for is still served by it provided the
    // package was in the scan and has not changed since. Anything else rebuilds: a key from a loader
    // it was warmed for with other bytes or another version, a path the build never scanned, a
    // package whose size or timestamp moved, and one whose bytes could not be named (#5081).
    internal static LoaderReuse JudgeLoaderReuse(
        IReadOnlyList<ServedDep> deps, HashSet<string>? universe, LoaderBuild? build)
    {
        var added = false;
        foreach (var d in deps)
        {
            if (universe != null && universe.Contains(d.Key)) continue;
            if (!d.Identified || build == null || build.DepPaths.Contains(d.Path)) return LoaderReuse.Rebuild;
            if (!build.InventoryStat.TryGetValue(d.Path, out var built) || StatOf(d.Path) != built)
                return LoaderReuse.Rebuild;
            added = true;
        }
        return added ? LoaderReuse.ReuseAndWarm : LoaderReuse.Reuse;
    }
}
