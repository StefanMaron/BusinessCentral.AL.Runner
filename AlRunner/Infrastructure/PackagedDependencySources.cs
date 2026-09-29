// PackagedDependencySources — coverage roots for a dependency consumed as a packaged `.app`
// that THIS run compiled from the package's embedded AL (DependencyLoader's Tier 3), #4273.
// Why only those, and what the report says about precompiled packages:
// docs/coverage-attribution.md#what-a-packaged-dependency-contributes-since-4273.
using System.Collections.Concurrent;

namespace AlRunner.Infrastructure;

internal static class PackagedDependencySources
{
    private static readonly ConcurrentDictionary<Guid, (string AppPath, string CacheKey)> _apps = new();

    /// <summary>
    /// Records that <paramref name="appId"/>'s code is the Tier-3 compile of the package at
    /// <paramref name="appPath"/>, cached under <paramref name="cacheKey"/>. Keyed by app id, so a
    /// package reloaded at a new version replaces its earlier entry rather than mapping twice.
    /// A Microsoft package is not recorded: the runner compiles Microsoft's Test Runner and test
    /// libraries this way on ordinary runs, and mapping them filled a one-codeunit fixture's
    /// report with the Test Runner's files (#4273).
    /// </summary>
    public static void Register(Guid appId, string publisher, string appPath, string cacheKey)
    {
        if (IsMicrosoft(publisher)) return;
        _apps[appId] = (appPath, cacheKey);
    }

    internal static bool IsMicrosoft(string publisher) =>
        string.Equals(publisher, "Microsoft", StringComparison.OrdinalIgnoreCase);

    internal static void ResetForTests() => _apps.Clear();

    internal static int RegisteredCount => _apps.Count;

    /// <summary>
    /// One directory per registered package holding the AL it was compiled from, written on first
    /// use. Called only when a coverage map is built, so a run without coverage never extracts.
    /// </summary>
    public static IReadOnlyList<string> Roots()
    {
        var roots = new List<string>();
        foreach (var (_, (appPath, cacheKey)) in _apps.OrderBy(kv => kv.Value.CacheKey, StringComparer.Ordinal))
            roots.Add(Materialize(appPath, cacheKey, CacheRoots.Resolve("compiled-deps")));
        return roots;
    }

    /// <summary>
    /// <c>&lt;cacheDir&gt;/&lt;cacheKey&gt;.src</c>: the package's <c>src/**/*.al</c> at their package
    /// paths, plus an <c>app.json</c> that declares nothing. The key is a hash of the package's
    /// content, so a directory that exists is this package's source; it is published by rename,
    /// so a reader never sees a half-written one.
    /// <para>Trap: the empty <c>app.json</c> is load-bearing. The Tier-3 compile read no manifest,
    /// so its preprocessor symbols are the no-manifest set; without a manifest here the source
    /// map would walk up to whatever app.json encloses the cache directory and parse under ITS
    /// symbols.</para>
    /// </summary>
    internal static string Materialize(string appPath, string cacheKey, string cacheDir)
    {
        var dir = Path.Combine(cacheDir, cacheKey + ".src");
        if (Directory.Exists(dir)) return dir;

        var staging = dir + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            File.WriteAllText(Path.Combine(staging, "app.json"), "{}");
            foreach (var (entryPath, source) in AppLoader.ExtractAlWithPaths(appPath))
            {
                var target = Path.Combine(staging, SafeRelativePath(entryPath));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, source);
            }
            try { Directory.Move(staging, dir); }
            catch (IOException) when (Directory.Exists(dir)) { /* another process published it first */ }
        }
        finally
        {
            if (Directory.Exists(staging))
                try { Directory.Delete(staging, recursive: true); } catch { /* scratch */ }
        }
        return dir;
    }

    /// <summary>A package entry path as a path that stays under the directory it is joined to:
    /// URL-decoded as package entries are (<c>src/Base64%2520Convert/…</c>), each segment
    /// stripped of characters a file name cannot hold, and never <c>.</c> or <c>..</c>.</summary>
    internal static string SafeRelativePath(string entryPath)
    {
        var bad = Path.GetInvalidFileNameChars();
        var segments = entryPath.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries).Select(segment =>
        {
            var decoded = segment;
            for (var i = 0; i < 2 && decoded.Contains('%'); i++) decoded = Uri.UnescapeDataString(decoded);
            var clean = new string(decoded.Select(c => Array.IndexOf(bad, c) >= 0 ? '_' : c).ToArray());
            return clean is "" or "." or ".." ? "_" : clean;
        });
        return Path.Combine(segments.ToArray());
    }
}
